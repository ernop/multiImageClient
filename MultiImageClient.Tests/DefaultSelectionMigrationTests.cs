#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MultiImageClient
{
    public sealed class DefaultSelectionMigrationTests : IDisposable
    {
        private const string Key = UiJobRunner.KeyIdeogramV45;
        private readonly string _root = Directory.CreateTempSubdirectory("mic-default-migration-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private Settings Settings(bool controller = false) => new()
        {
            ImageDownloadBaseFolder = _root,
            UiCommunityDbPath = Path.Combine(_root, "community.sqlite3"),
            UiEnvironmentController = controller,
        };

        private static UiGeneratorPreferencesRecord Preferences(
            string login, List<string> selected, List<string>? hidden = null) => new()
        {
            Login = login,
            DefaultView = "only-sota",
            ShowImageSection = true,
            ShowDescribeSection = false,
            HiddenGeneratorKeys = hidden ?? new List<string>(),
            DefaultSelectedKeys = selected,
            Presets = new List<UiGeneratorPresetRecord> { new() { Id = "p1", Name = "Mine", GeneratorKeys = new List<string> { "gpt2" } } },
            EndpointConfigurations = new List<UiGeneratorEndpointConfigurationRecord> { new() { Key = "gpt2", Notes = "Private" } },
            UpdatedAtUnixMs = 1234,
        };

        private UiEnvironmentRegistry Registry()
        {
            var path = Path.Combine(_root, "registry.json");
            File.WriteAllText(path, """
                {"version":1,"environments":[
                  {"id":"original","slug":"private-path","name":"Original","original":true,"members":["alice"],"defaultGenerators":["gpt2","ideogram"]},
                  {"id":"vibecoders-ai-generation","slug":"vibecoders-ai-generation","name":"Vibecoders","members":["bob"],"defaultGenerators":["gpt25-sunburst"]},
                  {"id":"third","slug":"third","name":"Third","members":[]}]}
                """);
            return new UiEnvironmentRegistry(path);
        }

        private UiLoginLinks LoginLinks(string defaults)
        {
            var path = Path.Combine(_root, "links.json");
            File.WriteAllText(path, "{\"version\":1,\"accounts\":[],\"defaultGenerators\":" + defaults + "}");
            return new UiLoginLinks(path);
        }

        [Fact]
        public void AccountListsGainTheKeyOnceAndKeepLaterRemovals()
        {
            var store = new UiCommunityStore(Settings());
            store.SaveGeneratorPreferences(Preferences("alice", new List<string> { "gpt2" }));
            store.SaveGeneratorPreferences(Preferences("bob", new List<string> { "gpt2" }, new List<string> { Key }));
            store.SaveGeneratorPreferences(Preferences("carol", new List<string> { Key, "ideogram" }));
            store.SaveGeneratorPreferences(Preferences("dave", new List<string>()));

            Assert.Equal(2, UiWorkflow.AddIdeogram45ToAccountDefaultsOnce(store));

            var alice = store.GetGeneratorPreferences("alice")!;
            Assert.Equal(new[] { "gpt2", Key }, alice.DefaultSelectedKeys);
            Assert.Equal("only-sota", alice.DefaultView);
            Assert.False(alice.ShowDescribeSection);
            Assert.Equal("Mine", Assert.Single(alice.Presets).Name);
            Assert.Equal("Private", Assert.Single(alice.EndpointConfigurations).Notes);
            Assert.Equal(1234, alice.UpdatedAtUnixMs);
            Assert.Equal(new[] { "gpt2" }, store.GetGeneratorPreferences("bob")!.DefaultSelectedKeys);
            Assert.Equal(new[] { Key, "ideogram" }, store.GetGeneratorPreferences("carol")!.DefaultSelectedKeys);
            Assert.Equal(new[] { Key }, store.GetGeneratorPreferences("dave")!.DefaultSelectedKeys);
            Assert.True(store.MigrationApplied(UiWorkflow.Ideogram45AccountDefaultsMigration));

            store.SaveGeneratorPreferences(Preferences("alice", new List<string> { "gpt2" }));
            var restarted = new UiCommunityStore(Settings());
            Assert.Equal(0, UiWorkflow.AddIdeogram45ToAccountDefaultsOnce(restarted));
            Assert.Equal(new[] { "gpt2" }, restarted.GetGeneratorPreferences("alice")!.DefaultSelectedKeys);
        }

        [Fact]
        public void MalformedAccountRowsFailWithoutChangingOrRecordingAnything()
        {
            var settings = Settings();
            var store = new UiCommunityStore(settings);
            store.SaveGeneratorPreferences(Preferences("alice", new List<string> { "gpt2" }));
            using (var connection = new SqliteConnection("Data Source=" + settings.UiCommunityDbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO ui_generator_preferences(login, show_image_section, show_describe_section,
                        hidden_generator_keys_json, default_selected_keys_json, presets_json, updated_at_unix_ms)
                    VALUES('broken', 1, 1, '[]', '{not json', '[]', 1);
                    """;
                command.ExecuteNonQuery();
            }

            var error = Assert.Throws<InvalidDataException>(() => UiWorkflow.AddIdeogram45ToAccountDefaultsOnce(store));
            Assert.Contains("'broken'", error.Message);
            Assert.Equal(new[] { "gpt2" }, store.GetGeneratorPreferences("alice")!.DefaultSelectedKeys);
            Assert.False(store.MigrationApplied(UiWorkflow.Ideogram45AccountDefaultsMigration));
        }

        [Fact]
        public void ControllerAddsTheKeyOnceToEveryRegistryList()
        {
            var settings = Settings(controller: true);
            var store = new UiCommunityStore(settings);
            var registry = Registry();

            Assert.Equal(2, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, registry, LoginLinks("[\"gpt2\"]")));

            Assert.Equal(new[] { "gpt2", "ideogram", Key }, registry.Get("original").DefaultGenerators);
            var vibecoders = registry.Get("vibecoders-ai-generation");
            Assert.Equal(new[] { "gpt25-sunburst", Key }, vibecoders.DefaultGenerators);
            Assert.Equal(new[] { "bob" }, vibecoders.Members);
            Assert.Null(registry.Get("third").DefaultGenerators);
            Assert.Equal("private-path", registry.Get("original").Slug);
            Assert.Equal(new[] { "gpt2" }, new UiLoginLinks(Path.Combine(_root, "links.json")).Defaults());

            vibecoders.DefaultGenerators = new List<string> { "gpt25-sunburst" };
            registry.Save(vibecoders);
            Assert.Equal(0, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, registry, null));
            Assert.Equal(new[] { "gpt25-sunburst" }, registry.Get("vibecoders-ai-generation").DefaultGenerators);
        }

        [Fact]
        public void OnlyTheListWriterMigratesEnvironmentLists()
        {
            var settings = Settings();
            var store = new UiCommunityStore(settings);
            var registry = Registry();
            Assert.Equal(0, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, registry, LoginLinks("[\"gpt2\"]")));
            Assert.Equal(new[] { "gpt2", "ideogram" }, registry.Get("original").DefaultGenerators);
            Assert.Equal(new[] { "gpt2" }, new UiLoginLinks(Path.Combine(_root, "links.json")).Defaults());
            Assert.False(store.MigrationApplied(UiWorkflow.Ideogram45EnvironmentDefaultsMigration));

            Assert.Equal(0, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, null, null));
            Assert.False(store.MigrationApplied(UiWorkflow.Ideogram45EnvironmentDefaultsMigration));
        }

        [Fact]
        public void StandaloneLoginLinkInstancesMigrateTheirOwnListOnce()
        {
            var settings = Settings();
            var store = new UiCommunityStore(settings);
            var links = LoginLinks("[\"gpt2\"]");

            Assert.Equal(1, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, null, links));
            Assert.Equal(new[] { "gpt2", Key }, links.Defaults());

            links.SetDefaults(new List<string> { "gpt2" });
            Assert.Equal(0, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, null, links));
            Assert.Equal(new[] { "gpt2" }, links.Defaults());
        }

        [Fact]
        public void AnUnsetLoginLinkListStaysUnset()
        {
            var settings = Settings();
            var store = new UiCommunityStore(settings);
            var links = LoginLinks("null");
            Assert.Equal(0, UiWorkflow.AddIdeogram45ToEnvironmentDefaultsOnce(settings, store, null, links));
            Assert.Null(links.Defaults());
            Assert.True(store.MigrationApplied(UiWorkflow.Ideogram45EnvironmentDefaultsMigration));
        }
    }
}
