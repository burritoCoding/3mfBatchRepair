// Explorer right-click "Repair with 3mfBatchRepair" entry for .stl/.obj/.3mf files, folders, and the empty space inside a folder.
// Lives under HKEY_CURRENT_USER, so it needs no admin rights and only affects this user.
// On Windows 11 it appears under "Show more options".
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

public static class ShellMenu
{
    static readonly string[] Keys =
    {
        @"Software\Classes\SystemFileAssociations\.stl\shell\3mfBatchRepair",
        @"Software\Classes\SystemFileAssociations\.obj\shell\3mfBatchRepair",
        @"Software\Classes\SystemFileAssociations\.3mf\shell\3mfBatchRepair",
        @"Software\Classes\Directory\shell\3mfBatchRepair",
        @"Software\Classes\Directory\Background\shell\3mfBatchRepair",   // empty space inside an open folder
    };

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    const int SHCNE_ASSOCCHANGED = 0x08000000;

    public static void Install(string guiExe)
    {
        foreach (string key in Keys)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
            {
                k.SetValue("", "Repair with 3mfBatchRepair");
                k.SetValue("Icon", "\"" + guiExe + "\"");
                // No MultiSelectModel: "Player" hides a plain command-line verb from Explorer's real menu (it wants a
                // DropTarget/DelegateExecute handler), though scripted verb lists still show it. Explorer starts one
                // process per selected item and the GUI gathers them into a single window (see SingleInstance).
                k.DeleteValue("MultiSelectModel", false);
                // %1 is the clicked file or folder; on a folder's background there's no item, %V is the folder itself.
                bool background = key.Contains(@"\Background\");
                using (RegistryKey c = k.CreateSubKey("command"))
                    c.SetValue("", "\"" + guiExe + "\" \"" + (background ? "%V" : "%1") + "\"");
            }
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Uninstall()
    {
        foreach (string key in Keys) Registry.CurrentUser.DeleteSubKeyTree(key, false);
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    // The exe the menu currently launches, or null if it isn't installed.
    public static string InstalledExe()
    {
        using (RegistryKey c = Registry.CurrentUser.OpenSubKey(Keys[0] + @"\command"))
        {
            string cmd = c != null ? c.GetValue("") as string : null;
            if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith("\"")) return null;
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd.Substring(1, end - 1) : null;   // no closing quote: not something we wrote
        }
    }
}
