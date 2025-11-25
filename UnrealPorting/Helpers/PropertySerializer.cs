using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.Math;

namespace UnrealPorting.Helpers
{
    public static class PropertySerializer
    {
        // ===================================================================
        // TOP-LEVEL UObject Serializer (FModel-style)
        // ===================================================================
        public static Dictionary<string, object?> SerializeUObject(UObject obj)
        {
            var root = new Dictionary<string, object?>();

            string type = obj.ExportType ?? obj.GetType().Name;
            string name = obj.Name?.ToString() ?? "(none)";

            root["Type"] = type;
            root["Name"] = name;
            root["Class"] = $"UScriptClass'{type}'";

            try
            {
                root["Flags"] = obj.Flags.ToString().Replace(", ", " | ");
            }
            catch
            {
                root["Flags"] = "";
            }

            var props = new Dictionary<string, object?>(StringComparer.Ordinal);

            // --------- GENERIC PROPERTIES FIRST ----------
            if (obj.Properties != null)
            {
                foreach (var p in obj.Properties)
                {
                    props[p.Name.ToString()] = ToJsonValue(p.Tag, p.Name.ToString());
                }
            }

            // -----------------------------------------------------------------
            // MATERIAL FALLBACK: read CachedExpressionData directly from Properties
            // -----------------------------------------------------------------
            if (obj.ExportType == "Material" && obj.Properties != null)
            {
                var cedProp = obj.Properties.Find(p => p.Name.Text == "CachedExpressionData");

                if (cedProp != null)
                {
                    var cedSf = ResolveFallbackStruct(cedProp.Tag?.GenericValue);
                    if (cedSf != null)
                    {
                        var runtimeProp = cedSf.Properties?.Find(p => p.Name.Text == "RuntimeEntries");
                        var runtimeSf = ResolveFallbackStruct(runtimeProp?.Tag?.GenericValue);

                        if (runtimeSf != null)
                        {
                            props["CachedExpressionData"] = HandleStruct(cedSf, "CachedExpressionData");
                        }
                        else
                        {
                            props["CachedExpressionData"] = HandleStruct(cedSf, "CachedExpressionData");
                        }
                    }
                }
            }

            // --------- SPECIAL CASE: MATERIAL INSTANCE CONSTANT ----------
            if (obj is UMaterialInstanceConstant mic)
            {
                props["ScalarParameterValues"] = SerializeScalarParams(mic);
                props["VectorParameterValues"] = SerializeVectorParams(mic);
            }

            // --------- SPECIAL CASE: UMaterial (ReferencedTextures) ----------
            if (obj is UMaterial mat)
            {
                var textureList = new List<object?>();

                // 1) Direct referenced textures
                if (mat.ReferencedTextures != null)
                {
                    foreach (var tex in mat.ReferencedTextures)
                    {
                        if (tex == null) continue;

                        string texName = tex.Name.ToString();
                        string texClass = tex.ExportType ?? tex.GetType().Name;
                        string texPath = tex.GetPathName() ?? "";

                        if (texPath.EndsWith($"{texName}.{texName}", StringComparison.OrdinalIgnoreCase))
                        {
                            texPath = texPath.Replace($"{texName}.{texName}", $"{texName}.0");
                        }

                        textureList.Add(new
                        {
                            ObjectName = $"{texClass}'{texName}'",
                            ObjectPath = texPath
                        });
                    }
                }

                // 2) Expressions → texture parameters
                foreach (var exprIndex in mat.Expressions)
                {
                    if (!exprIndex.TryLoad(out var exprObj))
                        continue;

                    foreach (var p in exprObj.Properties)
                    {
                        if (p.Tag is ObjectProperty objProp &&
                            objProp.Value != null &&
                            objProp.Value.TryLoad(out var texObj))
                        {
                            string texName = texObj.Name.ToString();
                            string texClass = texObj.ExportType ?? texObj.GetType().Name;
                            string texPath = texObj.GetPathName() ?? "";

                            if (texPath.EndsWith($"{texName}.{texName}", StringComparison.OrdinalIgnoreCase))
                            {
                                texPath = texPath.Replace($"{texName}.{texName}", $"{texName}.0");
                            }

                            textureList.Add(new
                            {
                                ObjectName = $"{texClass}'{texName}'",
                                ObjectPath = texPath
                            });
                        }
                    }
                }

                // 3) CachedExpressionData.ReferencedTextures
                if (mat.CachedExpressionData is FStructFallback ced)
                {
                    var texProp = ced.Properties?.Find(p => p.Name.Text == "ReferencedTextures");
                    if (texProp?.Tag is ArrayProperty arr && arr.Value?.Properties != null)
                    {
                        foreach (var elem in arr.Value.Properties)
                        {
                            if (elem.GenericValue is UObject texObj)
                            {
                                string texName = texObj.Name.ToString();
                                string texClass = texObj.ExportType ?? texObj.GetType().Name;
                                string texPath = texObj.GetPathName() ?? "";

                                if (texPath.EndsWith($"{texName}.{texName}", StringComparison.OrdinalIgnoreCase))
                                {
                                    texPath = texPath.Replace($"{texName}.{texName}", $"{texName}.0");
                                }

                                textureList.Add(new
                                {
                                    ObjectName = $"{texClass}'{texName}'",
                                    ObjectPath = texPath
                                });
                            }
                        }
                    }
                }

                // ------------------------------------------------------------
                // Restore FULL CachedExpressionData like FModel
                // ------------------------------------------------------------
                if (mat.CachedExpressionData is FStructFallback cedFull)
                {
                    props["CachedExpressionData"] = StructToJson(cedFull);
                }

                // FINAL WRITE
                if (textureList.Count > 0)
                    props["ReferencedTextures"] = textureList;
            }

            root["Properties"] = props;

            // Simple extras to resemble FModel a bit
            root["LoadedMaterialResources"] = new List<object>();
            root["CachedData"] = new Dictionary<string, object?>
            {
                ["ParentLayerIndexRemap"] = new List<object>()
            };

            // FINAL PASS: normalize any leftover "(FGuid)" / "(FLinearColor)" strings
            return (Dictionary<string, object?>)FixFinal(root)!;
        }

        // ===================================================================
        // MAIN PROPERTY VALUE SERIALIZER
        // ===================================================================
        public static object? ToJsonValue(FPropertyTagType? tag, string? propertyName = null)
        {
            if (tag == null)
                return null;

            var generic = tag.GenericValue;

            // 1) FIRST: string patterns (FGuid / FLinearColor)
            if (generic is string sGuid && sGuid.Contains("(FGuid)"))
            {
                string raw = sGuid.Split(' ')[0];
                return FormatFModelGuid(raw);
            }

            if (generic is string sColorLinear && sColorLinear.Contains("(FLinearColor)"))
            {
                string hex = sColorLinear.Split(' ')[0];
                return HexToColor(hex);
            }

            // If we actually get a real FLinearColor from some path, serialize it properly
            if (generic is FLinearColor lc)
                return SerializeFLinearColor(lc);

            // Hex-like string (no "(FLinearColor)") → color
            if (generic is string hexStr && IsHexStringColor(hexStr))
                return HexToColor(hexStr);

            // 2) UE5 StructProperty → unwrap StructType (real FStructFallback)
            if (tag is StructProperty sp)
            {
                var wrapper = sp.GenericValue;
                if (wrapper != null)
                {
                    var field = wrapper.GetType()
                        .GetField("StructType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (field?.GetValue(wrapper) is FStructFallback realSf)
                        return HandleStruct(realSf, propertyName);
                }
            }

            // 3) Direct FStructFallback or FScriptStruct wrapper
            if (generic is FStructFallback sfDirect)
                return HandleStruct(sfDirect, propertyName);

            if (generic != null && generic.GetType().Name == "FScriptStruct")
            {
                var sf = ExtractStructFallbackFromWrapper(generic);
                if (sf != null)
                    return HandleStruct(sf, propertyName);
            }

            // 4) Arrays
            if (tag is ArrayProperty arr && arr.Value?.Properties != null)
            {
                var list = new List<object?>();
                foreach (var elem in arr.Value.Properties)
                    list.Add(ToJsonValue(elem, propertyName));
                return list;
            }

            // 5) Object references (Parent, etc.)
            if (tag is ObjectProperty objProp)
            {
                var idx = objProp.Value;
                if (idx != null && idx.TryLoad(out var exportObj))
                {
                    string objName = exportObj.Name?.ToString() ?? "(null)";
                    string objPath = exportObj.GetPathName() ?? "";
                    string className = exportObj.ExportType ?? exportObj.GetType().Name;

                    if (!string.IsNullOrEmpty(objPath) &&
                        objPath.EndsWith($"{objName}.{objName}", StringComparison.OrdinalIgnoreCase))
                    {
                        objPath = objPath.Replace($"{objName}.{objName}", $"{objName}.0");
                    }

                    return new
                    {
                        ObjectName = $"{className}'{objName}'",
                        ObjectPath = objPath
                    };
                }

                return new
                {
                    ObjectName = "(null)",
                    ObjectPath = "(null)"
                };
            }

            // 6) Remaining primitives
            if (generic is string s)
                return s;

            if (generic is bool ||
                generic is byte or sbyte or short or ushort or int or uint or long or ulong ||
                generic is float or double)
                return generic;

            // 7) Fallback
            return generic?.ToString();
        }

        // ===================================================================
        // HANDLING FStructFallback (material params, colors, GUID structs)
        // ===================================================================
        private static object HandleStruct(FStructFallback sf, string? propertyName)
        {
            // Special handling for generic "ParameterValue" structs (non-MIC paths)
            if (propertyName == "ParameterValue")
            {
                sf = UnwrapSingleInnerStruct(sf);

                float? rVal = TryGetFloatDeep(sf, "R");
                float? gVal = TryGetFloatDeep(sf, "G");
                float? bVal = TryGetFloatDeep(sf, "B");
                float? aVal = TryGetFloatDeep(sf, "A");

                if (rVal != null || gVal != null || bVal != null || aVal != null)
                {
                    float r = rVal ?? 0f;
                    float g = gVal ?? 0f;
                    float b = bVal ?? 0f;
                    float a = aVal ?? 1f;

                    return new
                    {
                        R = r,
                        G = g,
                        B = b,
                        A = a,
                        Hex = $"{(int)r:X2}{(int)g:X2}{(int)b:X2}"
                    };
                }

                if (sf.Properties.Count == 1 &&
                    sf.Properties[0].Tag?.GenericValue is string fallback &&
                    fallback.Contains("(FLinearColor)"))
                {
                    string hex = fallback.Split(' ')[0];
                    return HexToColor(hex);
                }
            }

            // 1) Material parameter structs: ParameterInfo, ParameterValue, ExpressionGUID
            if (HasFields(sf, "ParameterInfo", "ParameterValue", "ExpressionGUID"))
            {
                var infoTag = sf.Properties.Find(x => x.Name.Text == "ParameterInfo")?.Tag;
                var valTag = sf.Properties.Find(x => x.Name.Text == "ParameterValue")?.Tag;
                var guidTag = sf.Properties.Find(x => x.Name.Text == "ExpressionGUID")?.Tag;

                return new Dictionary<string, object?>
                {
                    ["ParameterInfo"] = ToJsonValue(infoTag, "ParameterInfo"),
                    ["ParameterValue"] = ToJsonValue(valTag, "ParameterValue"),
                    ["ExpressionGUID"] = ToJsonValue(guidTag, "ExpressionGUID")
                };
            }

            // 2) Strict LinearColor
            if (IsStrictLinearColor(sf, out var colorOut))
                return colorOut!;

            // 3) Strict GUID
            if (IsStrictGuid(sf, out var guidOut))
                return guidOut;

            // 4) Generic struct → dictionary
            return StructToJson(sf);
        }

        // ===================================================================
        // MIC-SPECIFIC SERIALIZATION (REAL PARAMETER VALUES)
        // ===================================================================
        private static object SerializeFLinearColor(FLinearColor lc)
        {
            return new
            {
                R = lc.R,
                G = lc.G,
                B = lc.B,
                A = lc.A,
                Hex = lc.Hex
            };
        }

        private static List<object?> SerializeScalarParams(UMaterialInstanceConstant mic)
        {
            var result = new List<object?>();

            var scalars = mic.ScalarParameterValues;
            if (scalars == null)
                return result;

            foreach (var s in scalars)
            {
                if (s == null) continue;

                var info = SerializeParameterInfo(s.ParameterInfo);
                var value = s.ParameterValue;
                var guid = SerializeGuidLike(s.ExpressionGUID);

                result.Add(new Dictionary<string, object?>
                {
                    ["ParameterInfo"] = info,
                    ["ParameterValue"] = value,
                    ["ExpressionGUID"] = guid
                });
            }

            return result;
        }

        private static List<object?> SerializeVectorParams(UMaterialInstanceConstant mic)
        {
            var result = new List<object?>();

            var vectors = mic.VectorParameterValues;
            if (vectors == null)
                return result;

            foreach (var v in vectors)
            {
                if (v == null) continue;

                var info = SerializeParameterInfo(v.ParameterInfo);
                var value = v.ParameterValue.HasValue
                    ? SerializeFLinearColor(v.ParameterValue.Value)
                    : null;
                var guid = SerializeGuidLike(v.ExpressionGUID);

                result.Add(new Dictionary<string, object?>
                {
                    ["ParameterInfo"] = info,
                    ["ParameterValue"] = value,
                    ["ExpressionGUID"] = guid
                });
            }

            return result;
        }

        private static object SerializeParameterInfo(object? infoObj)
        {
            if (infoObj == null)
                return new Dictionary<string, object?>();

            var t = infoObj.GetType();
            var nameMember = (MemberInfo?)t.GetProperty("Name") ?? t.GetField("Name");
            var assocMember = (MemberInfo?)t.GetProperty("Association") ?? t.GetField("Association");
            var indexMember = (MemberInfo?)t.GetProperty("Index") ?? t.GetField("Index");

            object? GetMemberValue(MemberInfo? m)
            {
                if (m == null) return null;
                return m switch
                {
                    PropertyInfo pi => pi.GetValue(infoObj),
                    FieldInfo fi => fi.GetValue(infoObj),
                    _ => null
                };
            }

            var nameVal = GetMemberValue(nameMember);
            var assocVal = GetMemberValue(assocMember);
            var indexVal = GetMemberValue(indexMember);

            return new Dictionary<string, object?>
            {
                ["Name"] = nameVal?.ToString() ?? "",
                ["Association"] = assocVal?.ToString() ?? "",
                ["Index"] = indexVal ?? -1
            };
        }

        private static string SerializeGuidLike(object? guidVal)
        {
            if (guidVal == null)
                return "00000000-00000000-00000000-00000000";

            var t = guidVal.GetType();

            var fA = t.GetField("A", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fB = t.GetField("B", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fC = t.GetField("C", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fD = t.GetField("D", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (fA != null && fB != null && fC != null && fD != null &&
                fA.GetValue(guidVal) is uint A &&
                fB.GetValue(guidVal) is uint B &&
                fC.GetValue(guidVal) is uint C &&
                fD.GetValue(guidVal) is uint D)
            {
                string raw32 = $"{A:X8}{B:X8}{C:X8}{D:X8}";
                return FormatFModelGuid(raw32);
            }

            var text = guidVal.ToString() ?? "";
            var first = text.Split(' ')[0].Replace("-", "");
            return FormatFModelGuid(first);
        }

        // ===================================================================
        // STRUCT → DICTIONARY (generic fallback)
        // ===================================================================
        private static Dictionary<string, object?> StructToJson(FStructFallback sf)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (sf.Properties == null)
                return dict;

            foreach (var p in sf.Properties)
            {
                dict[p.Name.ToString()] = ToJsonValue(p.Tag, p.Name.ToString());
            }

            return dict;
        }

        // ===================================================================
        // TEXTURE HELPERS (still here, in case you use them later)
        // ===================================================================
        public static List<object?> ExtractTexturesFromExpressions(UMaterial mat)
        {
            var list = new List<object?>();

            if (mat.Expressions == null)
                return list;

            foreach (var exprIdx in mat.Expressions)
            {
                if (!exprIdx.TryLoad(out var expr))
                    continue;

                if (expr is UMaterialExpressionTextureSampleParameter tsp && tsp.Texture != null)
                {
                    list.Add(SerializeTextureRef(tsp.Texture));
                    continue;
                }

                if (expr is UMaterialExpressionTextureBase tb && tb.Texture != null)
                {
                    list.Add(SerializeTextureRef(tb.Texture));
                    continue;
                }
            }

            return list;
        }

        private static object SerializeTextureRef(UTexture tex)
        {
            string name = tex.Name.ToString();
            string className = tex.ExportType ?? tex.GetType().Name;
            string path = tex.GetPathName() ?? "";

            if (path.EndsWith($"{name}.{name}", StringComparison.OrdinalIgnoreCase))
                path = path.Replace($"{name}.{name}", $"{name}.0");

            return new
            {
                ObjectName = $"{className}'{name}'",
                ObjectPath = path
            };
        }

        // ===================================================================
        // Linear Color / GUID helpers
        // ===================================================================
        private static bool IsStrictLinearColor(FStructFallback sf, out object? output)
        {
            output = null;

            if (sf.Properties == null || sf.Properties.Count != 4)
                return false;

            bool hasR = false, hasG = false, hasB = false, hasA = false;
            float r = 0, g = 0, b = 0, a = 1;

            foreach (var p in sf.Properties)
            {
                switch (p.Name.Text)
                {
                    case "R" when p.Tag?.GenericValue is float rf:
                        hasR = true; r = rf;
                        break;
                    case "G" when p.Tag?.GenericValue is float gf:
                        hasG = true; g = gf;
                        break;
                    case "B" when p.Tag?.GenericValue is float bf:
                        hasB = true; b = bf;
                        break;
                    case "A" when p.Tag?.GenericValue is float af:
                        hasA = true; a = af;
                        break;
                }
            }

            if (!hasR || !hasG || !hasB || !hasA)
                return false;

            output = new
            {
                R = r,
                G = g,
                B = b,
                A = a,
                Hex = $"{(int)r:X2}{(int)g:X2}{(int)b:X2}"
            };

            return true;
        }

        private static bool IsStrictGuid(FStructFallback sf, out string? result)
        {
            result = null;

            if (sf.Properties == null || sf.Properties.Count != 4)
                return false;

            uint? A = null, B = null, C = null, D = null;

            foreach (var p in sf.Properties)
            {
                if (p.Tag?.GenericValue is not uint u)
                    return false;

                switch (p.Name.Text)
                {
                    case "A": A = u; break;
                    case "B": B = u; break;
                    case "C": C = u; break;
                    case "D": D = u; break;
                    default: return false;
                }
            }

            if (A == null || B == null || C == null || D == null)
                return false;

            string raw = $"{A:X8}{B:X8}{C:X8}{D:X8}";
            result = $"{raw[..8]}-{raw.Substring(8, 8)}-{raw.Substring(16, 8)}-{raw.Substring(24, 8)}";
            return true;
        }

        private static string FormatFModelGuid(string raw32)
        {
            raw32 = raw32.Replace("-", "").Trim();

            if (raw32.Length != 32)
                return raw32;

            return $"{raw32.Substring(0, 8)}-" +
                   $"{raw32.Substring(8, 8)}-" +
                   $"{raw32.Substring(16, 8)}-" +
                   $"{raw32.Substring(24, 8)}";
        }

        // ===================================================================
        // Helpers
        // ===================================================================
        private static bool HasFields(FStructFallback sf, params string[] names)
        {
            if (sf.Properties == null)
                return false;

            foreach (var name in names)
            {
                if (sf.Properties.Find(p => p.Name.Text == name) == null)
                    return false;
            }

            return true;
        }
        private static bool IsHexStringColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            return (s.Length == 6 || s.Length == 8) &&
                   int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        }

        private static object HexToColor(string hex)
        {
            hex = hex.Trim();

            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            byte a = 255;

            if (hex.Length == 8)
                a = Convert.ToByte(hex.Substring(6, 2), 16);

            return new
            {
                R = (float)r,
                G = (float)g,
                B = (float)b,
                A = (float)a,
                Hex = hex.Substring(0, 6)
            };
        }

        private static float? TryGetFloatDeep(FStructFallback sf, string name)
        {
            var p = sf.Properties.Find(x => x.Name.Text == name);
            if (p?.Tag?.GenericValue is float f)
                return f;

            if (sf.Properties.Count == 1 &&
                sf.Properties[0].Tag?.GenericValue is FStructFallback inner)
            {
                var innerProp = inner.Properties.Find(x => x.Name.Text == name);
                if (innerProp?.Tag?.GenericValue is float f2)
                    return f2;
            }

            return null;
        }

        private static FStructFallback UnwrapSingleInnerStruct(FStructFallback sf)
        {
            if (sf.Properties.Count == 1 &&
                sf.Properties[0].Tag?.GenericValue is FStructFallback inner)
            {
                return inner;
            }

            return sf;
        }

        private static FStructFallback? ExtractStructFallbackFromWrapper(object wrapper)
        {
            var t = wrapper.GetType();

            foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (prop.PropertyType == typeof(FStructFallback))
                    return prop.GetValue(wrapper) as FStructFallback;
            }

            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(FStructFallback))
                    return field.GetValue(wrapper) as FStructFallback;
            }

            return null;
        }

        private static FStructFallback? ResolveFallbackStruct(object? value)
        {
            if (value == null)
                return null;

            if (value is FStructFallback sf)
                return sf;

            var t = value.GetType();

            var p = t.GetProperty("StructType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var inner = p.GetValue(value);
                if (inner is FStructFallback innerSf)
                    return innerSf;
            }

            var f = t.GetField("StructType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                var inner = f.GetValue(value);
                if (inner is FStructFallback innerSf)
                    return innerSf;
            }

            return null;
        }

        // ===================================================================
        // FINAL PASS: fix any leftover "(FGuid)" / "(FLinearColor)" at any depth
        // ===================================================================
        private static object? FixFinal(object? value)
        {
            if (value == null)
                return null;

            if (value is string s)
            {
                if (s.Contains("(FGuid)"))
                {
                    string raw = s.Split(' ')[0];
                    return FormatFModelGuid(raw);
                }

                if (s.Contains("(FLinearColor)"))
                {
                    string hex = s.Split(' ')[0];
                    return HexToColor(hex);
                }

                return s;
            }

            if (value is Dictionary<string, object?> dict)
            {
                var fixedDict = new Dictionary<string, object?>(dict.Comparer);
                foreach (var kvp in dict)
                    fixedDict[kvp.Key] = FixFinal(kvp.Value);
                return fixedDict;
            }

            if (value is IList list)
            {
                var fixedList = new List<object?>(list.Count);
                foreach (var item in list)
                    fixedList.Add(FixFinal(item));
                return fixedList;
            }

            return value;
        }
    }
}
