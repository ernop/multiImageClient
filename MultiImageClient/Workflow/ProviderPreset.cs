using System;

namespace MultiImageClient
{
    public sealed class ProviderPreset
    {
        private readonly Func<IImageGenerator> _factory;

        public ProviderPreset(string id, string displayName, string group, Func<IImageGenerator> factory)
        {
            Id = id;
            DisplayName = displayName;
            Group = group;
            _factory = factory;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Group { get; }

        public IImageGenerator CreateGenerator() => _factory();
    }
}
