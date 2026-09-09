import argparse
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("environments", Path(__file__).with_name("create-environment.py"))
environments = importlib.util.module_from_spec(spec)
spec.loader.exec_module(environments)


class EnvironmentPreparationTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "Unix service permissions")
    def test_configuration_remains_group_readable_under_service_umask(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "config"
            previous = os.umask(0o077)
            try:
                with patch.object(environments.os, "chown") as chown:
                    environments.create_configuration_directory(path, 42)
                    chown.assert_called_once_with(path, 0, 42)
                self.assertEqual(path.stat().st_mode & 0o777, 0o750)
            finally:
                os.umask(previous)

    def test_preparation_does_not_copy_users_data_or_personal_settings(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "source.json"
            source.write_text(json.dumps({"OpenAIApiKey": "test-provider-key", "UiAuthFilePath": "/old/auth.json",
                "UiCommunityDbPath": "/old/community.sqlite3", "GenerationArchiveDbPath": "/old/archive.sqlite3",
                "ImageDownloadBaseFolder": "/old/images", "FlatImageMirrorPath": "/old/mirror",
                "DiscordVibecodersWebhookUrl": "https://old-webhook.test", "UiEnvironmentName": "Old group",
                "GrokWebCookiePath": "/old/cookies", "PromptFiles": ["/old/prompts.txt"]}))
            args = argparse.Namespace(id="studio", name="New studio", port=5961, memory_high_mib=512,
                memory_max_mib=768, max_requests=1, settings_source=str(source), output=str(root / "new"),
                providers="gpt2,recraft", nginx_site="/etc/nginx/sites-available/multiimageclient.conf",
                dotnet="/home/tparkour/.dotnet/dotnet", copy_grok_session=False)
            original = source.read_bytes()
            environments.prepare(args)
            settings = json.loads((root / "new/settings.json").read_text())
            self.assertEqual(settings["OpenAIApiKey"], "test-provider-key")
            self.assertNotIn("/old/", json.dumps(settings))
            self.assertEqual(settings["UiEnvironmentName"], "New studio")
            self.assertEqual(settings["PromptFiles"], [])
            self.assertEqual(settings["GrokWebCookiePath"], "")
            self.assertEqual(settings["DiscordVibecodersWebhookUrl"], "")
            links = json.loads((root / "new/login-links.json").read_text())
            self.assertEqual(len(links["accounts"]), 1)
            self.assertEqual(links["defaultGenerators"], ["gpt2", "recraft"])
            self.assertEqual(source.read_bytes(), original)
            with self.assertRaises(ValueError): environments.prepare(args)

    def test_only_adds_to_existing_tls_block(self):
        old = """server { listen 80; server_name multiimageclient.alpha.fuseki.net; location / { return 301 https://$host$request_uri; } }
server { listen 443 ssl http2; server_name multiimageclient.alpha.fuseki.net;
 # A comment with { braces }
 location /old-secret/ { proxy_pass http://127.0.0.1:5960/; }
}
server { listen 443 ssl; server_name other.test; location / { return 404; } }
"""
        include = "include /etc/nginx/multiimageclient-env-studio.locations;"
        result = environments.tls_include(old, include)
        self.assertEqual(result.replace("    " + include + "\n", ""), old)
        self.assertEqual(result.count(include), 1)
        with self.assertRaises(ValueError): environments.tls_include(result, include)
        with self.assertRaises(ValueError): environments.tls_include(old + old, include)

    def test_rejects_paths_as_environment_ids(self):
        for ident in ("../old", "studio/../../old", "UPPER", "a" * 25):
            with self.assertRaises(ValueError): environments.validate_id(ident)


if __name__ == "__main__": unittest.main()
