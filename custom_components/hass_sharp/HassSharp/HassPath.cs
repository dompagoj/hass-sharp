namespace HassSharp;

public class HassPath
{
    public static string HassConfiguration = string.Empty;
    public static string CustomComponents => Guard(Path.Join(HassConfiguration, "custom_components"));
    public static string HassSharpIntegration => Path.Join(CustomComponents, "hass_sharp");
    public static string UserScripts => Path.Join(HassSharpIntegration, "user_scripts");

    static string Guard(string actual)
    {
        if (HassConfiguration == string.Empty) throw new("HassPath.HassConfiguration not initialized");

        return actual;
    }
}
