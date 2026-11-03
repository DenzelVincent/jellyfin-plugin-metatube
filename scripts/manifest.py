#!/usr/bin/env python3
import copy
import hashlib
import json
import os
import sys
import xml.etree.ElementTree as ET
from datetime import datetime
from urllib.error import URLError
from urllib.request import Request, urlopen

from packaging.version import Version

# The repository that owns the plugin, e.g. "metatube-community/jellyfin-plugin-metatube".
# GitHub Actions sets GITHUB_REPOSITORY automatically, so a fork publishes a
# manifest that points at its own releases instead of the upstream ones.
REPOSITORY = os.environ.get('GITHUB_REPOSITORY',
                            'metatube-community/jellyfin-plugin-metatube')

SOURCE_URL = ('https://github.com/{repository}/releases/download/'
              'v{version}/Jellyfin.MetaTube@v{version}.zip')
IMAGE_URL = ('https://raw.githubusercontent.com/{repository}/'
             'main/Jellyfin.Plugin.MetaTube/thumb.png')
MANIFEST_URL = ('https://raw.githubusercontent.com/{repository}/dist/manifest.json')
RELEASES_API = ('https://api.github.com/repos/{repository}/releases?per_page=100')

# Fallback for the very first publish of a fork: the dist branch does not
# exist yet, so there is nothing to load. The guid must stay identical to
# Plugin.Id (Jellyfin.Plugin.MetaTube/Plugin.cs), otherwise the server would
# treat this as a different plugin.
SKELETON = [{
    'guid': '01cc53ec-c415-4108-bbd4-a684a9801a32',
    'name': 'MetaTube',
    'description': 'MetaTube 元数据插件（个人分支）：按系列与演员自动创建合集、'
                   '中文字幕与无码破解角标、字段级聚合与翻译。',
    'overview': 'Jellyfin/Emby 元数据插件，依赖 MetaTube Server 提供数据。',
    'owner': 'DenzelVincent',
    'category': 'Metadata',
    'imageUrl': '',
    'versions': []
}]


def md5sum(filename) -> str:
    with open(filename, 'rb') as f:
        return hashlib.md5(f.read()).hexdigest()


def md5sum_url(url: str) -> str:
    digest = hashlib.md5()
    with urlopen(url, timeout=60) as response:
        for chunk in iter(lambda: response.read(1 << 16), b''):
            digest.update(chunk)
    return digest.hexdigest()


def get_jellyfin_version(csproj: str) -> str:
    tree = ET.parse(csproj)
    root = tree.getroot()

    for pkg in root.iter("PackageReference"):
        if pkg.attrib.get("Include") in ("Jellyfin.Controller", "Jellyfin.Model"):
            return Version(pkg.attrib.get("Version")).base_version

    raise Exception("Jellyfin version not found")


def source_url(version: str) -> str:
    return SOURCE_URL.format(repository=REPOSITORY, version=version)


def generate(filename, version, csproj) -> dict:
    return {
        'checksum': md5sum(filename),
        'changelog': 'Auto Released by Actions',
        'targetAbi': f'{get_jellyfin_version(csproj)}.0',
        'sourceUrl': source_url(version),
        'timestamp': datetime.now().strftime('%Y-%m-%dT%H:%M:%SZ'),
        'version': version
    }


def get_releases() -> list:
    """Releases of this repository that ship a Jellyfin plugin zip."""
    request = Request(RELEASES_API.format(repository=REPOSITORY),
                      headers={'Accept': 'application/vnd.github+json'})
    token = os.environ.get('GITHUB_TOKEN')
    if token:
        request.add_header('Authorization', f'Bearer {token}')

    with urlopen(request, timeout=30) as response:
        releases = json.load(response)

    result = []
    for release in releases:
        if release.get('draft') or not release.get('tag_name'):
            continue
        version = release['tag_name'].removeprefix('v')
        asset = f'Jellyfin.MetaTube@v{version}.zip'
        assets = {a['name']: a.get('browser_download_url')
                  for a in release.get('assets', [])}
        if not assets.get(asset):
            continue
        result.append({
            'version': version,
            'asset': assets[asset],
            'timestamp': (release.get('published_at') or
                          datetime.now().strftime('%Y-%m-%dT%H:%M:%SZ')),
        })
    return result


def load_manifest() -> tuple[list, bool]:
    """Published manifest and whether the built-in skeleton had to stand in.

    Returns ``(manifest, used_skeleton)``: the flag says the seed was
    unreachable, so ``versions`` starts out empty.

    A fork's first publish has no dist branch yet, and a network hiccup must
    not abort a release that already built successfully.
    """
    try:
        with urlopen(MANIFEST_URL.format(repository=REPOSITORY), timeout=30) as f:
            return json.load(f), False
    except (URLError, OSError, ValueError) as e:
        # HTTPError 是 URLError 的子类；JSON 解析失败走 ValueError。
        print(f'warning: published manifest unavailable, using skeleton: {e}; '
              'a 404 normally means the dist branch does not exist yet, i.e. '
              'this fork is publishing for the first time', file=sys.stderr)
        return copy.deepcopy(SKELETON), True


def main() -> None:
    filename = sys.argv[1]
    version = filename.split('@', maxsplit=1)[1] \
        .removeprefix('v') \
        .removesuffix('.zip')

    csproj = os.path.join(os.path.dirname(__file__),
                          "../Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj")

    manifest, used_skeleton = load_manifest()

    plugin = manifest[0]
    plugin['imageUrl'] = IMAGE_URL.format(repository=REPOSITORY)
    existing = {entry['version']: entry for entry in plugin['versions']}

    try:
        releases = get_releases()
    except (URLError, OSError, ValueError) as e:
        # 两处兜底叠在一起就会产出错的目录：种子读不到时 plugin['versions'] 为空，
        # releases API 又失败，写出的 manifest 只剩本次构建，CI 强推 dist 分支后
        # 已发布的历史会被抹光。宁肯这次发布失败，也不发出没有历史的 manifest。
        if used_skeleton:
            print(f'error: no published manifest to keep and releases cannot be '
                  f'listed, so the catalog would lose every previously published '
                  f'version: {e}', file=sys.stderr)
            sys.exit(1)
        # Do not break the release when the API is unavailable: keep the
        # previous entries, only the new one gets the corrected URL.
        print(f'warning: cannot list releases: {e}', file=sys.stderr)
        versions = [entry for entry in plugin['versions']
                    if entry['version'] != version]
        for entry in versions:
            entry['sourceUrl'] = source_url(entry['version'])
    else:
        versions = []
        for release in releases:
            if release['version'] == version:
                continue  # the current build is prepended below
            entry = existing.get(release['version'])
            if entry is None:
                # Release missing from the previous manifest: rebuild the
                # entry so forks keep the whole history instead of only the
                # versions that upstream happened to list.
                entry = {
                    'checksum': md5sum_url(release['asset']),
                    'changelog': 'Auto Released by Actions',
                    'targetAbi': f'{get_jellyfin_version(csproj)}.0',
                    'timestamp': release['timestamp'],
                    'version': release['version'],
                }
            entry['sourceUrl'] = source_url(release['version'])
            versions.append(entry)

    plugin['versions'] = [generate(filename, version, csproj)] + versions

    with open('manifest.json', 'w') as f:
        json.dump(manifest, f, indent=2)


if __name__ == '__main__':
    main()
