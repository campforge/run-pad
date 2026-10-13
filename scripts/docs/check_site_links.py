#!/usr/bin/env python3
"""Checks the generated documentation site (mkdocs build output) for broken internal links.

Every href/src/srcset in every HTML page is resolved the way a browser would resolve it on
GitHub Pages, where the site lives under a path prefix (/run-pad/):

- relative URLs resolve against the page's own location;
- root-relative URLs must start with the prefix and are resolved below it;
- absolute URLs on the site's own origin (canonical links, sitemap) are treated as internal;
- a #fragment must match an id (or a[name]) in the target page.

Assets the page loads (stylesheets, scripts, images, icons, fonts) must be served from the
site itself, so the published docs make no third-party requests. Plain links to other sites
are allowed and not fetched.

Usage: check_site_links.py SITE_DIR --site-url https://coddwrench.github.io/run-pad/
Exits 1 if any problem is found.
"""

from __future__ import annotations

import argparse
import posixpath
import sys
import xml.etree.ElementTree as ET
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit

# Attributes that make the browser fetch a resource (as opposed to a link the user follows).
LOADED = {("link", "href"), ("script", "src"), ("img", "src"), ("source", "src"),
          ("iframe", "src"), ("video", "src"), ("audio", "src"), ("embed", "src")}
LINK_ATTRS = {"href", "src", "srcset"}
IGNORED_SCHEMES = ("mailto:", "tel:", "javascript:", "data:", "blob:")
# <link rel=...> values that are hints or metadata rather than something the page loads.
NON_LOADING_RELS = {"canonical", "alternate", "prev", "next", "license", "author", "search"}


class PageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.ids: set[str] = set()
        self.refs: list[tuple[str, str, str, bool]] = []  # (tag, attr, url, is_loaded_asset)

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {name: value for name, value in attrs if value is not None}
        if "id" in values:
            self.ids.add(values["id"])
        if tag == "a" and "name" in values:
            self.ids.add(values["name"])

        rels = set(values.get("rel", "").lower().split())
        for attr in LINK_ATTRS & values.keys():
            urls = [part.strip().split()[0] for part in values[attr].split(",") if part.strip()] \
                if attr == "srcset" else [values[attr].strip()]
            loaded = attr == "srcset" or (tag, attr) in LOADED
            if tag == "link" and rels & NON_LOADING_RELS:
                loaded = False
            for url in urls:
                self.refs.append((tag, attr, url, loaded))


def parse(path: Path) -> PageParser:
    parser = PageParser()
    parser.feed(path.read_text(encoding="utf-8"))
    return parser


def main() -> int:
    arg_parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    arg_parser.add_argument("site_dir", type=Path)
    arg_parser.add_argument("--site-url", required=True,
                            help="Public URL of the site root, e.g. https://coddwrench.github.io/run-pad/")
    args = arg_parser.parse_args()

    site_dir: Path = args.site_dir.resolve()
    site_url = args.site_url if args.site_url.endswith("/") else args.site_url + "/"
    site = urlsplit(site_url)
    origin = f"{site.scheme}://{site.netloc}"
    prefix = site.path  # "/run-pad/"

    if not (site_dir / "index.html").is_file():
        print(f"error: {site_dir} has no index.html; build the site first", file=sys.stderr)
        return 1

    pages = sorted(site_dir.rglob("*.html"))
    parsed = {page: parse(page) for page in pages}
    problems: list[str] = []
    checked = external_links = 0

    def resolve(target_path: str) -> Path | None:
        """Maps a site path below the prefix (e.g. /run-pad/editor/) to a file, or None."""
        relative = unquote(target_path[len(prefix):])
        candidate = (site_dir / relative).resolve()
        if site_dir not in candidate.parents and candidate != site_dir:
            return None
        if target_path.endswith("/") or candidate.is_dir():
            candidate = candidate / "index.html"
        return candidate if candidate.is_file() else None

    def check(source: str, page_url: str, url: str, loaded: bool) -> None:
        nonlocal checked, external_links
        if not url or url.startswith(IGNORED_SCHEMES):
            return
        absolute = urljoin(page_url, url)
        parts = urlsplit(absolute)
        if f"{parts.scheme}://{parts.netloc}" != origin:
            if loaded:
                problems.append(f"{source}: loads an external resource: {url}")
            else:
                external_links += 1
            return
        checked += 1
        if not parts.path.startswith(prefix):
            problems.append(f"{source}: link leaves the {prefix} prefix: {url}")
            return
        target = resolve(parts.path)
        if target is None:
            problems.append(f"{source}: broken link: {url}")
            return
        if parts.fragment and target.suffix == ".html":
            target_ids = parsed[target].ids if target in parsed else parse(target).ids
            if unquote(parts.fragment) not in target_ids:
                problems.append(f"{source}: missing anchor #{parts.fragment} in {url}")

    for page in pages:
        relative = page.relative_to(site_dir).as_posix()
        page_url = origin + posixpath.join(prefix, relative)
        for tag, attr, url, loaded in parsed[page].refs:
            check(f"{relative} <{tag} {attr}>", page_url, url, loaded)

    sitemap = site_dir / "sitemap.xml"
    if sitemap.is_file():
        namespace = {"sm": "http://www.sitemaps.org/schemas/sitemap/0.9"}
        for loc in ET.parse(sitemap).getroot().findall("sm:url/sm:loc", namespace):
            url = (loc.text or "").strip()
            if not url.startswith(site_url):
                problems.append(f"sitemap.xml: URL outside {site_url}: {url}")
            else:
                check("sitemap.xml", site_url, url, loaded=False)

    for problem in problems:
        print(problem)
    print(f"Checked {len(pages)} pages, {checked} internal links and assets, "
          f"{external_links} external links (not fetched): {len(problems)} problem(s).")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
