"""Offline contract checks for the neutral static-site payload."""
from html.parser import HTMLParser
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1] / "src" / "OpenJibo.Site"


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()
        self.h1 = 0
        self.lang = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.lang = attrs.get("lang")
        if tag == "h1":
            self.h1 += 1
        if "id" in attrs:
            self.ids.add(attrs["id"])
        for key in ("href", "src"):
            if key in attrs:
                self.links.append(attrs[key])


class PublicSiteTests(unittest.TestCase):
    def test_accessible_structure_and_local_links(self):
        for path in ROOT.glob("*.html"):
            with self.subTest(page=path.name):
                parsed = Page()
                parsed.feed(path.read_text(encoding="utf-8"))
                self.assertEqual(parsed.lang, "en")
                self.assertEqual(parsed.h1, 1)
                self.assertIn("main", parsed.ids)
                self.assertIn("#main", parsed.links)
                for link in parsed.links:
                    if link.startswith("#"):
                        self.assertIn(link[1:], parsed.ids)
                    elif not link.startswith("https://"):
                        file, _, anchor = link.partition("#")
                        self.assertNotIn(":", file)
                        target = ROOT / file
                        self.assertTrue(target.is_file(), link)
                        if anchor:
                            other = Page()
                            other.feed(target.read_text(encoding="utf-8"))
                            self.assertIn(anchor, other.ids)

    def test_downloads_are_targeted_preview(self):
        page = (ROOT / "downloads.html").read_text(encoding="utf-8")
        for profile in ("portable", "avx2"):
            self.assertIn(f"runtime-preview-36863178102/starter-{profile}-preview.zip", page)
        for checksum in ("68f7181b4c50b08c632a482efd9f0a20be3ab3a2ad1de97fe6281a5e320b6a91", "eaab909b46e1166d08344c7582fb3833fe9bdc596a005e0ce4ee4a8c3bdb6466"):
            self.assertIn(checksum, page)
        self.assertIn("Not a stable release", page)
        self.assertIn("127.0.0.1:8082:8080", page)
        self.assertIn("operator-reviewed", page)

    def test_payload_has_no_operational_files(self):
        self.assertEqual({p.name for p in ROOT.iterdir()}, {"index.html", "downloads.html", "site.css"})


if __name__ == "__main__":
    unittest.main()
