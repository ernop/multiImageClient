#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiImageClient
{
    // The controller is the only writer. Other instances read the same bounded registry.
    public sealed class UiEnvironmentRegistry
    {
        public sealed class Environment
        {
            public required string Id { get; init; }
            public required string Slug { get; set; }
            public required string Name { get; set; }
            public bool Original { get; init; }
            public bool GoalLoops { get; set; } = true;
            public bool Video { get; set; } = true;
            public bool PromptRewrite { get; set; } = true;
            public bool NightFilter { get; set; } = true;
            public bool? VibecodersSharing { get; set; }
            [JsonIgnore] public bool AllowVibecoders => VibecodersSharing ?? Original;
            public List<string> Members { get; set; } = new();
            public List<string>? DefaultGenerators { get; set; }
        }
        public sealed class Document
        {
            public int Version { get; init; } = 1;
            public List<Environment> Environments { get; set; } = new();
        }
        private readonly string _path;
        private readonly object _sync = new();
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false };
        public UiEnvironmentRegistry(string path) { _path = Path.GetFullPath(path); Read(); }
        public Document Read()
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 1048576)
                throw new InvalidDataException("Environment registry is missing or too large.");
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), Options)
                ?? throw new InvalidDataException("Environment registry is empty.");
            Validate(doc); return doc;
        }
        public Environment Get(string id) => Read().Environments.SingleOrDefault(e => e.Id == id)
            ?? throw new InvalidDataException("Unknown environment.");
        public bool CanEnter(string id, string login) => login == UiLoginLinks.OwnerLogin || Get(id).Members.Contains(login, StringComparer.Ordinal);
        public void Save(Environment value)
        {
            lock (_sync)
            {
                var doc = Read(); var old = doc.Environments.SingleOrDefault(e => e.Id == value.Id);
                if (old != null && (old.Original != value.Original || old.Original && old.Slug != value.Slug))
                    throw new InvalidDataException("The original environment route cannot change here.");
                if (old == null && value.Original) throw new InvalidDataException("The original environment already exists.");
                if (old != null) doc.Environments.Remove(old);
                doc.Environments.Add(value); Validate(doc);
                var temp = _path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                    JsonSerializer.Serialize(stream, doc, Options); stream.Flush(true);
                }
                File.Move(temp, _path, true);
            }
        }
        private static void Validate(Document doc)
        {
            if (doc.Version != 1 || doc.Environments == null || doc.Environments.Count is < 1 or > 16
                || doc.Environments.Count(e => e.Original) != 1)
                throw new InvalidDataException("Invalid environment registry.");
            var ids = new HashSet<string>(); var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Environments)
            {
                if (e == null || !Regex.IsMatch(e.Id ?? "", "^[a-z][a-z0-9-]{0,23}$") || !ids.Add(e.Id)
                    || !Regex.IsMatch(e.Slug ?? "", "^[a-z0-9][a-z0-9-]{0,63}$") || !slugs.Add(e.Slug)
                    || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 80 || e.Name.Any(char.IsControl)
                    || e.Members == null || e.Members.Count > 500 || e.Members.Any(string.IsNullOrWhiteSpace)
                    || e.Members.Distinct(StringComparer.Ordinal).Count() != e.Members.Count
                    || e.DefaultGenerators?.Count > 64)
                    throw new InvalidDataException("Invalid environment name, URL name, or membership.");
            }
        }
    }
}
