namespace NetPilot;

public static class AtomicStore
{
    private static readonly object Gate = new();
    public static T? Read<T>(string path)
    {
        lock (Gate)
        {
            if (!File.Exists(path)) return default;
            try { return JsonData.Decode<T>(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            { throw new InvalidDataException("Повреждён файл " + Path.GetFileName(path) + ". Исходный файл сохранён.",ex); }
        }
    }
    public static void Write<T>(string path,T value,Action<string>? prepareFile=null)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                { var bytes=System.Text.Encoding.UTF8.GetBytes(JsonData.Encode(value)); f.Write(bytes); f.Flush(true); }
                // Elevated helpers protect ownership/ACLs before this file can become
                // the durable journal; a crash never publishes an untrusted primary.
                prepareFile?.Invoke(temporary);
                if (File.Exists(path)) File.Replace(temporary,path,path+".bak",true);
                else File.Move(temporary,path);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

public static class AppPaths
{
    public const string ServiceName = "NetPilotDns";
    public const string PipeName = "NetPilotDns.Control.v1";
    public static string LocalData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NetPilot");
    public static string ServiceData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"NetPilot");
    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NetPilot");
    public static string InstalledExe => Path.Combine(InstallDirectory,"RtlineDnsPilot.exe");
    public static string LegacyInstalledExe => Path.Combine(InstallDirectory,"NetPilot.exe");
}
