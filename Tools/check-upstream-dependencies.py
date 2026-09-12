"""Read stable upstream releases for a manual compatibility review; never update packages."""
import argparse
import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPOSITORIES = {'lilToon': 'lilxyzw/lilToon', 'UniVRM': 'vrm-c/UniVRM',
                'NDMF': 'bdunderscore/ndmf', 'Modular Avatar': 'bdunderscore/modular-avatar'}


def release_version(tag):
    value = tag[1:] if tag.startswith('v') else tag
    if not re.fullmatch(r'\d+\.\d+\.\d+', value):
        raise ValueError('Unrecognized stable release tag')
    return tuple(map(int, value.split('.')))


def compare(config, releases):
    known = {'lilToon': config['lilToon']['version'],
             'UniVRM': max(config['uniVrm']['versions'], key=release_version),
             'NDMF': config['authoring']['ndmfReference'],
             'Modular Avatar': config['authoring']['modularAvatarReference']}
    result = []
    for name in REPOSITORIES:
        release = releases[name]
        if release.get('draft') or release.get('prerelease'):
            raise ValueError('Expected a published stable release: ' + name)
        tag = release['tag_name']
        result.append({'package': name, 'reference': known[name], 'upstream': tag,
                       'needsCompatibilityReview': release_version(tag) > release_version(known[name])})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    config = json.loads((ROOT / 'Compatibility/dependencies.json').read_text(encoding='utf-8'))
    releases = {}
    for name, repository in REPOSITORIES.items():
        request = urllib.request.Request('https://api.github.com/repos/' + repository + '/releases/latest',
                                         headers={'Accept': 'application/vnd.github+json', 'User-Agent': 'VRVlog-compatibility'})
        with urllib.request.urlopen(request, timeout=20) as response:
            data = response.read(2 * 1024 * 1024 + 1)
            if len(data) > 2 * 1024 * 1024:
                raise ValueError('Release response exceeds limit')
            releases[name] = json.loads(data)
    result = compare(config, releases)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    for item in result:
        state = 'REVIEW NEEDED' if item['needsCompatibilityReview'] else 'no newer release'
        print(f"{item['package']}: {item['reference']} -> {item['upstream']} ({state})")
    print('Release availability is not proof of compatibility. No dependency or support policy was changed.')


if __name__ == '__main__':
    main()
