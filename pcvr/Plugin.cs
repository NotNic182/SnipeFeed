using BeatSaberMarkupLanguage.GameplaySetup;
using BeatSaberMarkupLanguage.Util;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using IPA.Logging;
using SnipeFeed.PC.Configuration;
using SnipeFeed.PC.UI;
using System;
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
            try
            {
                await MainMenuAwaiter.WaitForMainMenuAsync();
                RegisterGameplayTab();
                MainMenuAwaiter.MainMenuInitializing += RegisterGameplayTab;
            }
            catch (Exception ex)
            {
                // BSIPA does not observe this task; without the catch a
                // startup failure is a silently missing tab with no log line.
                Log?.Error("SnipeFeed failed to start: " + ex);
            }
        }

        [OnExit]
        public void OnExit()
        {
            MainMenuAwaiter.MainMenuInitializing -= RegisterGameplayTab;
        }

        private static void RegisterGameplayTab()
        {
            try
            {
                var setup = GameplaySetup.Instance;
                if (setup == null)
                {
                    Log?.Warn("GameplaySetup.Instance is null; the Snipe Feed tab was not added this menu load.");
                    return;
                }
                if (ReferenceEquals(setup, _registeredGameplaySetup)) return;

                setup.AddTab(
                    "Snipe Feed",
                    "SnipeFeed.PC.UI.SnipeFeedView.bsml",
                    View,
                    MenuType.All);

                _registeredGameplaySetup = setup;
                Log?.Info("Registered Snipe Feed gameplay setup tab.");
            }
            catch (Exception ex)
            {
                // Thrown from BSML's MainMenuInitializing multicast this
                // would abort every later subscriber (other mods included).
                Log?.Error("Registering the Snipe Feed tab failed: " + ex);
            }
        }
    }
}
