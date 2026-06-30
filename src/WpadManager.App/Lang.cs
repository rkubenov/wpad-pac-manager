namespace WpadManager.App
{
    internal enum AppLang { Ru, En }

    // Tiny two-language switch. Every user-facing string is written inline as L.T(ru, en);
    // there are no .resx resource files, so the build stays a single zero-dependency exe.
    // The choice is persisted in the workspace and applied by rebuilding the window.
    internal static class L
    {
        public static AppLang Current = AppLang.Ru;

        public static string T(string ru, string en)
        {
            return Current == AppLang.En ? en : ru;
        }

        public static string Code()
        {
            return Current == AppLang.En ? "en" : "ru";
        }

        public static void Set(string code)
        {
            Current = (code != null && code.Trim().ToLowerInvariant() == "en")
                ? AppLang.En : AppLang.Ru;
        }
    }
}
