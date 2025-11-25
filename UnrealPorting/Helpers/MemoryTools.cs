using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Runtime;

namespace UnrealPorting.Helpers
{
    internal static class MemoryTools
    {
        public static void ForceCue4ParseCleanup()
        {
            try
            {
                Console.WriteLine("[DEBUG] Forcing deep CUE4Parse cleanup...");

                var asm = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => a.FullName.StartsWith("CUE4Parse", StringComparison.OrdinalIgnoreCase));

                if (asm != null)
                {
                    // Try to clear all static caches (PakFileCache, FileProviderCache, etc.)
                    foreach (var type in asm.GetTypes())
                    {
                        // Only static classes
                        var fields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        foreach (var field in fields)
                        {
                            var fType = field.FieldType;

                            // Clear dictionaries, lists, or caches
                            if (typeof(IDictionary).IsAssignableFrom(fType))
                            {
                                if (field.GetValue(null) is IDictionary dict)
                                {
                                    int count = dict.Count;
                                    dict.Clear();
                                    if (count > 0)
                                        Console.WriteLine($"[DEBUG] Cleared {count} entries from {type.Name}.{field.Name}");
                                }
                            }
                            else if (typeof(ICollection).IsAssignableFrom(fType))
                            {
                                if (field.GetValue(null) is ICollection col && col.Count > 0)
                                {
                                    if (col is IList list)
                                        list.Clear();
                                    Console.WriteLine($"[DEBUG] Cleared {col.Count} items from {type.Name}.{field.Name}");
                                }
                            }
                        }
                    }
                }

                // Force deep GC cleanup for unmanaged buffers
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Console.WriteLine("[DEBUG] CUE4Parse cleanup completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Cleanup error: {ex.Message}");
            }
        }
    }
}
