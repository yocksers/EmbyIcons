using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Common;
using System;
using MediaBrowser.Controller.Plugins;

namespace EmbyIcons
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>
    {
        private readonly ILibraryManager _libraryManager;

        public static Plugin? Instance { get; private set; }

        public ILibraryManager LibraryManager => _libraryManager;

        public Plugin(IApplicationHost appHost, ILibraryManager libraryManager)
            : base(appHost)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            Instance = this;
        }

        public override string Name => "EmbyIcons";

        public override string Description => "Overlays language icons onto media posters.";

        public override Guid Id => new("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");

        public PluginOptions GetConfiguredOptions() => GetOptions();
    }
}
