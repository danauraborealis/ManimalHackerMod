namespace Manimal.HackerMod
{
    // mirror of the server-side constants. keep in sync with HackerModServer.
    public static class HackerConstants
    {
        public const string HackerDeviceTpl    = "69f95efeb93f3b8fa8d3879f"; // green path only
        public const string RefHackerDeviceTpl = "69fd2542b93f3b8fa8d387a0"; // adds blue path: 3 blue hits = crack, no use consumed

        public static readonly string[] AllHackerDeviceTpls =
        {
            HackerDeviceTpl,
            RefHackerDeviceTpl,
        };

        public const string RublesTpl = "5449016a4bdc2d6f028b456f"; // ruble stack

        // ATM payout tuning
        public const int AtmGreenStackSize = 500;
        public const int AtmBlueStackSize  = 1000;
        public const int AtmMinStackCount  = 4;
        public const int AtmMaxStackCount  = 8;

        public static bool HasBluePath(string tpl) => tpl == RefHackerDeviceTpl;

        // canvas Z-rotation for rendering content onto the phone screen mesh.
        // different device meshes have different UV orientations so the rotation
        // that lands text horizontally varies per device.
        public static float GetCanvasRotation(string tpl)
        {
            if (tpl == RefHackerDeviceTpl) return 180f;
            return 90f;
        }

        // (0,0) = auto-detect from mesh aspect. hardcode if auto-detect picks wrong.
        public static (int width, int height) GetCanvasSize(string tpl)
        {
            return (0, 0);
        }
    }
}
