using BeatSaberMarkupLanguage.GameplaySetup;
using BeatSaberMarkupLanguage.Util;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using IPA.Logging;
using SnipeFeed.PC.Configuration;
using SnipeFeed.PC.UI;
using System.Threading.Tasks;

namespace SnipeFeed.PC
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static Logger Log { get; private set; }
        private static GameplaySetup _registeredGameplaySetup;
        private static readonly SnipeFeedView View = new SnipeFeedView();

        [Init]
        public Plugin(Logger logger, Config config)
        {
            Log = logger;
            PluginConfig.Instance = config.Generated<PluginConfig>();
            Log.Info("SnipeFeed PCVR initialized.");
        }

        [OnStart]
        public async Task OnStart()
        {
            await MainMenuAwaiter.WaitForMainMenuAsync();
            RegisterGameplayTab();
            MainMenuAwaiter.MainMenuInitializing += RegisterGameplayTab;
        }

        [OnExit]
        public void OnExit()
        {
            MainMenuAwaiter.MainMenuInitializing -= RegisterGameplayTab;
        }

        private static void RegisterGameplayTab()
        {
            var setup = GameplaySetup.Instance;
            if (setup == null || ReferenceEquals(setup, _registeredGameplaySetup)) return;

            setup.AddTab(
                "Snipe Feed",
                "SnipeFeed.PC.UI.SnipeFeedView.bsml",
                View,
                MenuType.All);

            _registeredGameplaySetup = setup;
            Log?.Info("Registered Snipe Feed gameplay setup tab.");
        }
    }
}
