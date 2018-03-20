namespace FiveLoavesAndTwoFish {
    public class Config {
        public bool FetchPics { get; set; } = false;
        public int HistoryPosts { get; set; } = 100;
        public int RefreshPosts { get; set; } = 10;
        public int CheckInterval { get; set; } = 1;
        public string Token { get; set; }
        public string[] Groups { get; set; }
        public string ListenAddress { get; set; } = "http://127.0.0.1:31337";
        public bool AdminOnly { get; set; } = false;
    }
}