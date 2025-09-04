using System;
using System.Reflection;

internal static class Program
{
    private static void Main(string[] args)
    {
        var rwDir = Environment.GetEnvironmentVariable("RIMWORLD_DIR");
        var asmPath = System.IO.Path.Combine(rwDir, "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll");
        var asm = Assembly.LoadFrom(asmPath);
        var t = asm.GetType("RimWorld.SoundDefOf", throwOnError:false);
        foreach (var f in t.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
        {
            if (f.Name.IndexOf("Rotate", StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine(f.Name);
        }
    }
}
