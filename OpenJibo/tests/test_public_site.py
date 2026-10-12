"""Offline contract checks for the neutral static-site payload."""
from html.parser import HTMLParser
from pathlib import Path
import importlib.util
import os
import json
import re
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1] / "src" / "OpenJibo.Site"


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()
        self.h1 = 0
        self.lang = None
        self.duplicate_ids = []
        self.copy_targets = []
        self.commands = {}
        self.current_command = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.lang = attrs.get("lang")
        if tag == "h1":
            self.h1 += 1
        if "id" in attrs:
            if attrs["id"] in self.ids:
                self.duplicate_ids.append(attrs["id"])
            self.ids.add(attrs["id"])
        if tag == "button" and "data-copy" in attrs:
            self.copy_targets.append(attrs["data-copy"])
        if tag == "code" and attrs.get("id", "").endswith("-command"):
            self.current_command = attrs["id"]
            self.commands[self.current_command] = ""
        for key in ("href", "src"):
            if key in attrs:
                self.links.append(attrs[key])

    def handle_data(self, data):
        if self.current_command:
            self.commands[self.current_command] += data

    def handle_endtag(self, tag):
        if tag == "code":
            self.current_command = None


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
                self.assertFalse(parsed.duplicate_ids)
                for target in parsed.copy_targets:
                    self.assertIn(target, parsed.commands)
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
        self.assertIn("loopback port", page)
        self.assertIn("operator-reviewed", page)

    def test_payload_has_no_operational_files(self):
        self.assertEqual({p.name for p in ROOT.iterdir()}, {"index.html", "downloads.html", "site.css"})

    def test_signed_preview_is_primary_without_claiming_stable_certification(self):
        page = (ROOT / "downloads.html").read_text(encoding="utf-8")
        self.assertLess(page.index('id="signed-preview"'), page.index('id="legacy-preview"'))
        for profile in ("portable", "avx2"):
            self.assertIn(f"runtime-preview-38165346370/starter-{profile}-preview.zip", page)
            self.assertIn(f"runtime-preview-38165346370/starter-{profile}-attestation.json", page)
        for digest in ("e8a63cc2ebe27be0499dc6ec5e235382b0063fcd26fc8caeb9e536adc5414cb8", "660b599a89ac71056699cbf66a1ac76f492c3b3d662702e8f5a3a1ab31daa569"):
            self.assertIn(digest, page)
        self.assertIn("gh attestation verify --help", page)
        self.assertIn("set -euo pipefail", page)
        self.assertIn("--port 8093", page)
        self.assertIn("--port 8094", page)
        self.assertIn("--cpu-profile avx2", page)
        self.assertIn("--avx2-local-daemon", page)
        self.assertNotIn("automatic preparer/launcher currently supports portable only", page)
        self.assertIn("--plan", page)
        self.assertIn("--start", page)
        self.assertIn("checksum-only ZIPs", page)
        self.assertIn("encrypted-data decryption", page)

    def test_primary_downloads_match_reviewed_preparer_pins(self):
        script = ROOT.parents[1] / "scripts/cloud/prepare-published-starter.py"
        spec = importlib.util.spec_from_file_location("site_reviewed_preparer", script)
        preparer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(preparer)
        page = (ROOT / "downloads.html").read_text(encoding="utf-8")
        primary = page.split('id="signed-preview"', 1)[1].split('</section>', 1)[0]
        for profile in ('portable', 'avx2'):
            record = preparer.release_identity(preparer.GUARDED_VERSION, profile)
            self.assertIn(record["url"], primary)
            self.assertIn(record["archive_sha256"], primary)
            self.assertIn("--version " + record["version"], page)
            self.assertTrue(record["release_zip_attested"])
        for old in (preparer.VERSION, preparer.SIGNED_VERSION, preparer.AUDIO_VERSION):
            self.assertNotIn(old, primary)

    def test_promotion_keeps_experimental_limits_and_prior_releases(self):
        page = (ROOT / 'downloads.html').read_text(encoding='utf-8')
        for phrase in ('not clearance or stable readiness', 'License/notice review',
                       'physical-robot acceptance remain open', 'existing-token survival',
                       'No latest/stable selector', 'operator-reviewed'):
            self.assertIn(phrase, page)
        for version in ('runtime-preview-37540893708', 'runtime-preview-38139481435'):
            self.assertIn('/releases/tag/' + version, page)

    def test_unprovisioned_cloud_domain_is_not_a_live_signup_link(self):
        page = (ROOT / 'index.html').read_text(encoding='utf-8')
        self.assertIn('cloud.openjibo.com', page)
        self.assertIn('public DNS setup is still', page)
        self.assertNotIn('href="https://cloud.openjibo.com/', page)
        self.assertIn('does not promise public signup or pricing', page)

    def test_guided_path_keeps_robot_help_separate_from_server_installation(self):
        home = (ROOT / 'index.html').read_text(encoding='utf-8')
        for phrase in ('I have a Jibo.', 'I want to try OpenJibo.',
                       'does not change or convert a physical robot',
                       'Public signup is not open yet.', 'jiborevived.com'):
            self.assertIn(phrase, home)
        page = (ROOT / 'downloads.html').read_text(encoding='utf-8')
        for anchor in ('check', 'prepare', 'launch', 'verify', 'help'):
            self.assertIn(f'id="{anchor}"', page)
        for phrase in ('Keep this terminal open', 'health_passed',
                       'The <code>--plan</code> output alone does not verify',
                       'Keep it—especially its private settings and data'):
            self.assertIn(phrase, page)

    def test_guided_commands_keep_fresh_names_and_nondestructive_stop(self):
        page = Page()
        page.feed((ROOT / 'downloads.html').read_text(encoding='utf-8'))
        prepare = page.commands['prepare-command']
        launch = page.commands['launch-command']
        stop = page.commands['stop-command']
        self.assertIn('preview_project="openjibo-preview-$(date -u +%Y%m%d%H%M%S)"', prepare)
        self.assertIn('preview_dir="$HOME/Downloads/$preview_project"', prepare)
        self.assertIn('--destination "$preview_dir" --plan', prepare)
        self.assertIn('--destination "$preview_dir"', prepare)
        self.assertIn('--directory "$preview_dir"', launch)
        self.assertIn('--project "$preview_project" --port 8093', launch)
        self.assertIn('${preview_dir:?Run step 2 in this terminal first}', launch)
        self.assertIn('preview-launch.compose.yaml" stop', stop)
        for command in page.commands.values():
            self.assertNotRegex(command, r'\b(?:rm|prune)\b|down\s+-v|curl[^\n]*\|\s*(?:sh|bash)')

    def test_copy_controls_without_a_real_clipboard(self):
        node = shutil.which('node')
        if not node:
            self.skipTest('Node is required for the copy-control behavior check')
        raw = (ROOT / 'downloads.html').read_text(encoding='utf-8')
        script = re.search(r'<script>([\s\S]*?)</script>', raw).group(1)
        harness = r'''
const vm = require('node:vm');
const script = JSON.parse(process.argv[1]);
(async () => {
  for (const scenario of ['success','denied','insecure']) {
    let copied = null, callback = null;
    const button = {hidden:true,dataset:{copy:'command'},addEventListener:(_,fn)=>callback=fn};
    const status = {textContent:''};
    const context = {window:{isSecureContext:scenario!=='insecure',setTimeout:()=>{}},
      navigator:{clipboard:{writeText:async text=>{if(scenario==='denied')throw Error('denied');copied=text;}}},
      document:{querySelectorAll:()=>[button],getElementById:id=>id==='command'?{textContent:'safe test command'}:status}};
    vm.runInNewContext(script,context);
    if(scenario==='insecure'){if(!button.hidden||callback)throw Error('insecure copy exposed');continue;}
    if(button.hidden||!callback)throw Error('copy unavailable');
    await callback();
    if(scenario==='success'&&(copied!=='safe test command'||!status.textContent.includes('copied')))throw Error('copy failed');
    if(scenario==='denied'&&!status.textContent.includes('manually'))throw Error('missing fallback');
  }
})().catch(error=>{console.error(error.message);process.exitCode=1});
'''
        result = subprocess.run([node, '-e', harness, json.dumps(script)],
                                text=True, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_guided_commands_have_valid_bash_syntax(self):
        git_bash = Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Git/bin/bash.exe'
        bash = str(git_bash) if git_bash.is_file() else shutil.which('bash')
        if not bash:
            self.skipTest('Bash is required for command syntax checks')
        parsed = Page()
        parsed.feed((ROOT / 'downloads.html').read_text(encoding='utf-8'))
        for name, command in parsed.commands.items():
            with self.subTest(command=name):
                result = subprocess.run([bash, '-n'], input=command, text=True,
                                        capture_output=True, timeout=15)
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_core_text_colors_meet_normal_text_contrast(self):
        css = (ROOT / 'site.css').read_text(encoding='utf-8')
        colors = dict(re.findall(r'--([a-z]+):\s*(#[0-9a-f]{6})', css))

        def luminance(value):
            channels = [int(value[i:i + 2], 16) / 255 for i in (1, 3, 5)]
            linear = [c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in channels]
            return sum(c * w for c, w in zip(linear, (.2126, .7152, .0722)))

        for foreground, background in [('ink', 'paper'), ('muted', 'paper'),
                                      ('accent', 'paper'), ('white', 'ink'),
                                      ('muted', 'mint')]:
            with self.subTest(foreground=foreground, background=background):
                values = sorted([luminance(colors[foreground]), luminance(colors[background])])
                self.assertGreaterEqual((values[1] + .05) / (values[0] + .05), 4.5)


if __name__ == "__main__":
    unittest.main()
