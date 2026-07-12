#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MultiImageClient
{
    /// Reverse-engineered meta.ai consumer-session cookie loader. Mirrors
    /// GrokWebCookieLoader: it accepts a raw Cookie-header line, a Chrome
    /// DevTools "Application > Cookies" copy, a Netscape cookies.txt export,
    /// or simple name=value lines, and produces one Cookie header string.
    ///
    /// meta.ai auth is cookie-only (no API key). The load-bearing cookies are
    /// the browser identifier `datr` plus a session cookie — current builds use
    /// `abra_sess`, older ones `ecto_1_sess`. We require `datr` and at least one
    /// session cookie; everything else Meta sets is passed through untouched
    /// because the persisted-query endpoint is picky about the full jar.
    public static class MetaWebCookieLoader
    {
        private const string BrowserIdCookie = "datr";
        private static readonly string[] SessionCookies = { "abra_sess", "ecto_1_sess" };

        public static string LoadCookieHeader(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Cookie path is empty.", nameof(path));
            }

            var expanded = Settings.ExpandPath(path);
            if (!File.Exists(expanded))
            {
                throw new FileNotFoundException($"Meta web cookie file not found: {expanded}", expanded);
            }

            var text = File.ReadAllText(expanded).Trim().Trim('\'', '"');
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Meta web cookie file is empty: {expanded}");
            }

            // Raw Cookie header pasted from the Network tab: datr=...; abra_sess=...; ...
            if (!text.Contains('\n') && text.Contains('=') && text.Contains(';'))
            {
                ValidateRequiredCookies(ParseHeaderPairs(text));
                return text.Trim();
            }

            var pairs = ParseCookieFile(text);
            if (pairs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Could not parse any cookies from {expanded}. Paste DevTools Application cookies, Netscape cookies.txt, or a raw Cookie header line.");
            }

            ValidateRequiredCookies(pairs);
            return string.Join("; ", pairs.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        private static Dictionary<string, string> ParseCookieFile(string text)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (!line.Contains('\t'))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        AddCookie(pairs, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                    }
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    continue;
                }

                // Chrome DevTools Application > Cookies copy: Name, Value, Domain, Path, ...
                if (LooksLikeDevToolsCookieRow(parts))
                {
                    var domain = parts.Length > 2 ? parts[2].Trim() : "";
                    if (!IsMetaDomain(domain))
                    {
                        continue;
                    }

                    AddCookie(pairs, parts[0].Trim(), parts[1].Trim());
                    continue;
                }

                // Netscape cookies.txt: domain, flag, path, secure, expiration, name, value
                if (parts.Length >= 7)
                {
                    var domain = parts[0].Trim();
                    if (!IsMetaDomain(domain))
                    {
                        continue;
                    }

                    AddCookie(pairs, parts[5].Trim(), parts[6].Trim());
                }
            }

            return pairs;
        }

        private static Dictionary<string, string> ParseHeaderPairs(string header)
        {
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var segment in header.Split(';'))
            {
                var piece = segment.Trim();
                if (piece.Length == 0)
                {
                    continue;
                }

                var eq = piece.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                pairs[piece.Substring(0, eq).Trim()] = piece.Substring(eq + 1).Trim();
            }

            return pairs;
        }

        private static bool LooksLikeDevToolsCookieRow(string[] parts)
        {
            var first = parts[0].Trim();
            if (first.Length == 0 || first.StartsWith('.') || first.Contains(' '))
            {
                return false;
            }

            // Netscape rows start with a domain in column 0.
            if (parts.Length >= 7 && parts[0].TrimStart('.').Contains('.'))
            {
                return false;
            }

            return parts.Length >= 3;
        }

        private static bool IsMetaDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return false;
            }

            var d = domain.TrimStart('.');
            return d.Equals("meta.ai", StringComparison.OrdinalIgnoreCase)
                   || d.EndsWith(".meta.ai", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCookie(Dictionary<string, string> pairs, string name, string value)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
            {
                return;
            }

            pairs[name] = value;
        }

        private static void ValidateRequiredCookies(Dictionary<string, string> pairs)
        {
            var missing = new List<string>();
            if (!pairs.ContainsKey(BrowserIdCookie))
            {
                missing.Add(BrowserIdCookie);
            }

            if (!SessionCookies.Any(pairs.ContainsKey))
            {
                missing.Add("a session cookie (" + string.Join(" or ", SessionCookies) + ")");
            }

            if (missing.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "Meta web cookie file is missing required auth cookies: "
                + string.Join(", ", missing)
                + ". document.cookie is not enough — copy from DevTools > Application > Cookies > https://www.meta.ai and include HttpOnly rows like datr and abra_sess.");
        }
    }
}
