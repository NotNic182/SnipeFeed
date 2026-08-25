namespace SnipeFeed.PC.Configuration
{
    public class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        public virtual string PlayerId { get; set; } = "";
        public virtual int MaxPlayers { get; set; } = 20;
        public virtual int ScoresPerPlayer { get; set; } = 3;
        public virtual int FeedCount { get; set; } = 50;
    }
}
