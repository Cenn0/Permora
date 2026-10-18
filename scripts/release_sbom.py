"""Merge CycloneDX inventories without silently dropping roots or dangling edges.

A source root (for example the scanned OCI image) is evidence, not a library
package. Retain it as a component and namespace every component reference so
separate images cannot overwrite each other's dependency relationships.
"""
from __future__ import annotations
import copy
import re


def components_in(component):
    yield component
    for child in component.get('components', []):
        yield from components_in(child)


def validate_graph(document):
    if document.get('bomFormat') != 'CycloneDX' or document.get('version') != 1:
        raise ValueError('Expected a versioned CycloneDX inventory')
    roots = list(document.get('components', []))
    root = document.get('metadata', {}).get('component')
    if root:
        roots.append(root)
    seen = set()
    for parent in roots:
        for component in components_in(parent):
            ref = component.get('bom-ref')
            if not isinstance(ref, str) or not ref or ref in seen:
                raise ValueError('Missing or duplicate component reference')
            if not component.get('name') or not component.get('type'):
                raise ValueError('Component lacks required name/type')
            seen.add(ref)
    for edge in document.get('dependencies', []):
        for ref in [edge.get('ref'), *edge.get('dependsOn', []), *edge.get('provides', [])]:
            if ref not in seen:
                raise ValueError('Dependency refers to an undefined component')
    return len(seen)


def merge(source_sha: str, sources: dict[str, dict]) -> dict:
    if not re.fullmatch('[0-9a-f]{40}', source_sha) or not sources:
        raise ValueError('A pinned source commit and nonempty inventories are required')
    combined_components, combined_edges, source_roots = [], [], []
    for name, source in sorted(sources.items()):
        if source.get('bomFormat') != 'CycloneDX' or source.get('specVersion') != '1.6':
            raise ValueError('Only verified CycloneDX 1.6 scanner output may be merged')
        doc = copy.deepcopy(source)
        root = doc.get('metadata', {}).get('component')
        if not root or not doc.get('components'):
            raise ValueError('The scanner must identify its source root and nonempty inventory')
        root.setdefault('bom-ref', 'source-root')
        all_roots = [root, *doc['components']]
        refs = {}
        for parent in all_roots:
            for component in components_in(parent):
                old = component.get('bom-ref')
                if not old or old in refs:
                    raise ValueError('Scanner source has missing or duplicate references')
                refs[old] = name + ':' + old
        source_roots.append(refs[root['bom-ref']])
        for parent in all_roots:
            for component in components_in(parent):
                component['bom-ref'] = refs[component['bom-ref']]
                component.setdefault('properties', []).append({'name': 'permora:evidence-source', 'value': name})
            combined_components.append(parent)
        for edge in doc.get('dependencies', []):
            try:
                value = {'ref': refs[edge['ref']], 'dependsOn': [refs[ref] for ref in edge.get('dependsOn', [])]}
                if 'provides' in edge:
                    value['provides'] = [refs[ref] for ref in edge['provides']]
            except KeyError as error:
                raise ValueError('Scanner dependency references an undefined component') from error
            combined_edges.append(value)
    root_ref = 'permora:' + source_sha
    result = {'bomFormat': 'CycloneDX', 'specVersion': '1.6', 'version': 1,
        'metadata': {'component': {'bom-ref': root_ref, 'type': 'application', 'name': 'Permora', 'version': source_sha},
            'properties': [{'name': 'permora:scope', 'value': 'actual-api-worker-provisioner-images-plus-production-frontend-closure'},
                           {'name': 'permora:production-approval', 'value': 'false'}]},
        'components': combined_components,
        'dependencies': [{'ref': root_ref, 'dependsOn': source_roots}, *combined_edges]}
    validate_graph(result)
    return result
