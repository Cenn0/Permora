#!/usr/bin/env python3
"""Check security invariants of the intentionally non-deployable K8s reference.

Use yq and kubectl preinstalled on official ubuntu-24.04 GitHub runners.
Static schema/structure checks are NOT real ingress, cluster, secret
management, key rotation, or high-availability acceptance.
"""
from __future__ import annotations
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "deploy/k8s/permora.reference.yaml"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError("Kubernetes reference: " + message)


def load_documents() -> list[dict]:
    proc = subprocess.run(
        ["yq", "eval", "-o=json", "--no-doc", ".", str(SOURCE)],
        check=True, capture_output=True, text=True, timeout=30
    )
    decoder = json.JSONDecoder()
    documents = []
    s = proc.stdout.strip()
    while s:
        obj, cursor = decoder.raw_decode(s)
        require(isinstance(obj, dict), "resource document must be an object")
        documents.append(obj)
        s = s[cursor:].lstrip()
    require(len(documents) >= 14, "reference must include all HA and isolation resources")
    return documents


def validate() -> None:
    subprocess.run(["kubectl", "kustomize", str(ROOT / "deploy/k8s")],
                   check=True, stdout=subprocess.DEVNULL, timeout=45)
    docs = load_documents()
    names = set()
    for doc in docs:
        require("apiVersion" in doc and "kind" in doc and "metadata" in doc,
                "each resource must have an explicit API/kind/name")
        key = (doc["kind"], doc["metadata"].get("namespace", ""), doc["metadata"].get("name"))
        require(key[2] and key not in names, "resource identifiers must be unique")
        names.add(key)
        if doc["kind"] not in ("Namespace",):
            require(doc["metadata"].get("namespace") == "permora", "workloads must stay in the isolated namespace")
        if doc["kind"] == "Secret":
            raise AssertionError("Real Kubernetes Secret values must not be checked into Git")

    kinds = {}
    for doc in docs:
        kinds[(doc["kind"], doc["metadata"]["name"])] = doc

    namespace = kinds["Namespace", "permora"]
    require(namespace["metadata"]["labels"].get("pod-security.kubernetes.io/enforce") == "restricted",
            "namespace must enforce restricted PodSecurity")
    claim = kinds["PersistentVolumeClaim", "permora-data-protection-keys"]
    require("ReadWriteMany" in claim["spec"]["accessModes"],
            "Data Protection key ring must be backed by independently validated RWX storage")
    service = kinds["Service", "permora-api"]
    require(service["spec"]["type"] == "ClusterIP", "do not expose the API via NodePort/LoadBalancer")
    require(len([x for x in docs if x["kind"] == "Service"]) == 1, "workers/provisioner must have no network service")
    ingress = kinds["Ingress", "permora-api"]
    require(ingress["spec"].get("tls") and ingress["spec"]["tls"][0]["secretName"],
            "TLS ingress secret must be explicitly managed")
    require(ingress["spec"]["ingressClassName"] == "nginx", "explicit external ingress controller required")
    require(ingress["spec"]["rules"][0]["host"].endswith(".invalid"),
            "reference host must be non-routable until intentionally customized")

    workloads = {
        "permora-api": 2,
        "permora-worker": 2,
        "permora-provisioner": 0,
    }
    for name, replicas in workloads.items():
        deployment = kinds["Deployment", name]
        require(deployment["spec"]["replicas"] == replicas,
                "correct replica count required; provisioner must default to disabled")
        template = deployment["spec"]["template"]
        pod = template["spec"]
        require(pod["serviceAccountName"] == name and not pod["automountServiceAccountToken"],
                "no Kubernetes API token may be mounted automatically")
        sec = pod["securityContext"]
        require(sec.get("runAsNonRoot") is True and sec.get("runAsUser") != 0
                and sec.get("seccompProfile", {}).get("type") == "RuntimeDefault",
                "non-root and runtime-default seccomp required")
        require(len(pod["containers"]) == 1, "reference processes are separated")
        container = pod["containers"][0]
        context = container["securityContext"]
        require(context.get("allowPrivilegeEscalation") is False
                and context.get("readOnlyRootFilesystem") is True
                and "ALL" in context.get("capabilities", {}).get("drop", []),
                "containers must drop privileges, capabilities, writable root")
        require(re.fullmatch(r"registry\.example\.invalid/permora/(api|worker|provisioner)@sha256:[a-f0-9]{64}", container["image"]),
                "all images must be explicit content digests; no mutable tags")
        require(not re.search(r"@sha256:(?!0{64})", container["image"]),
                "reference must refuse to run until reviewed real image digests are supplied")
        require(any(v.get("name") == "runtime" and v.get("readOnly") is True
                    for v in container["volumeMounts"]),
                "secrets must be mounted read-only")
        require(any(v.get("name") == "temp" and v.get("emptyDir", {}).get("medium") == "Memory"
                    for v in pod["volumes"]), "temporary filesystem must be memory-backed")
        require(not any(v.get("hostPath") for v in pod["volumes"]),
                "no hostPath/socket mounts allowed")
        require(any(v.get("name") == "runtime" and v.get("secret", {}).get("secretName") == name + "-runtime"
                    for v in pod["volumes"]), "distinct credential mounts by process")
        if name in ("permora-api", "permora-worker"):
            spread = pod.get("topologySpreadConstraints", [])
            require({s["topologyKey"] for s in spread} >=
                    {"topology.kubernetes.io/zone", "kubernetes.io/hostname"},
                    "API/worker replicas require zone and host spreading")
            require(all(s["whenUnsatisfiable"] == "DoNotSchedule" and s["minDomains"] >= 2
                        for s in spread), "HA replica placement must fail closed on a single domain")
        if name == "permora-api":
            require(any(v.get("persistentVolumeClaim", {}).get("claimName") == "permora-data-protection-keys"
                        for v in pod["volumes"]), "shared encrypted DataProtection keys required")
            require(container.get("readinessProbe", {}).get("httpGet", {}).get("path") == "/health/ready",
                    "DB health check required for API readiness")
        else:
            require(not any(v.get("persistentVolumeClaim") for v in pod["volumes"]),
                    "worker and provisioner must not access browser encryption keys")

    for target in ("permora-api", "permora-worker"):
        budget = kinds["PodDisruptionBudget", target]
        require(budget["spec"]["minAvailable"] == 1, "at least one ready copy must survive planned disruption")
    all_deny = kinds["NetworkPolicy", "deny-all"]
    require(all_deny["spec"]["podSelector"] == {} and
            set(all_deny["spec"]["policyTypes"]) == {"Ingress", "Egress"},
            "default deny all ingress and egress required")
    allow = kinds["NetworkPolicy", "allow-only-api-ingress"]
    require(allow["spec"]["podSelector"]["matchLabels"]["app.kubernetes.io/component"] == "api",
            "worker/provisioner cannot accept inbound ingress")
    egress = kinds["NetworkPolicy", "allow-bounded-permora-egress"]
    destination_cidrs = {target["ipBlock"]["cidr"] for rule in egress["spec"]["egress"]
                         for target in rule.get("to", []) if "ipBlock" in target}
    require(destination_cidrs == {"192.0.2.0/24", "198.51.100.0/24"},
            "placeholder egress stays reserved/unroutable until centrally approved")
    require(not any("0.0.0.0/0" in str(rule) or "::/0" in str(rule)
                    for rule in egress["spec"]["egress"]), "internet-open egress forbidden")
    print(f"Kubernetes reference: {len(docs)} resources validated; no cluster deployed or approved")


if __name__ == "__main__":
    validate()
