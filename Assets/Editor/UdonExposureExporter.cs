#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Udon.Editor;

namespace UdonSharp.Editors
{
    public static class UdonExposureExporter
    {
        private static class CompilerUdonInterfaceProxy
        {
            private static readonly Type CompilerInterfaceType =
                Type.GetType("UdonSharp.Compiler.Udon.CompilerUdonInterface, UdonSharp.Editor");

            private static readonly Type FieldAccessorType =
                CompilerInterfaceType?.GetNestedType("FieldAccessorType", BindingFlags.NonPublic | BindingFlags.Public);

            private static readonly MethodInfo GetUdonTypeNameMethod =
                CompilerInterfaceType?.GetMethod("GetUdonTypeName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);

            private static readonly MethodInfo GetUdonMethodNameMethod =
                CompilerInterfaceType?.GetMethod("GetUdonMethodName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase) }, null);

            private static readonly MethodInfo IsExposedToUdonMethod =
                CompilerInterfaceType?.GetMethod("IsExposedToUdon", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

            private static readonly MethodInfo GetUdonAccessorNameMethod =
                CompilerInterfaceType?.GetMethod("GetUdonAccessorName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(FieldInfo), FieldAccessorType }, null);

            public static bool IsAvailable =>
                CompilerInterfaceType != null &&
                FieldAccessorType != null &&
                GetUdonTypeNameMethod != null &&
                GetUdonMethodNameMethod != null &&
                IsExposedToUdonMethod != null &&
                GetUdonAccessorNameMethod != null;

            public static string GetUdonTypeName(Type type)
            {
                if (!IsAvailable || type == null) return null;
                return GetUdonTypeNameMethod.Invoke(null, new object[] { type }) as string;
            }

            public static string GetUdonMethodName(MethodBase method)
            {
                if (!IsAvailable || method == null) return null;
                return GetUdonMethodNameMethod.Invoke(null, new object[] { method }) as string;
            }

            public static bool IsExposedToUdon(string signature)
            {
                if (!IsAvailable || string.IsNullOrEmpty(signature)) return false;
                object result = IsExposedToUdonMethod.Invoke(null, new object[] { signature });
                return result is bool exposed && exposed;
            }

            public static string GetUdonAccessorName(FieldInfo field, string accessorName)
            {
                if (!IsAvailable || field == null || string.IsNullOrEmpty(accessorName)) return null;
                object accessorValue = Enum.Parse(FieldAccessorType, accessorName);
                return GetUdonAccessorNameMethod.Invoke(null, new[] { (object)field, accessorValue }) as string;
            }
        }

        private struct MemberExposureInfo
        {
            public string Namespace;
            public string TypeName;
            public string TypeKind;       // class, struct, enum, array
            public string MemberKind;     // Method, Property, Field, Constructor
            public bool IsStatic;
            public string Signature;
            public string UdonName;
            public bool Exposed;
        }

        [MenuItem("Tools/Export Exposure Lists")]
        private static void ExportExposureLists()
        {
            try
            {
                if (!CompilerUdonInterfaceProxy.IsAvailable)
                {
                    Debug.LogError("[UdonExposureExporter] Could not load UdonSharp compiler exposure APIs via reflection.");
                    return;
                }

                EditorUtility.DisplayProgressBar("Udon Exposure Export", "Initializing...", 0f);

                // Step 1: Build exposed type set (same logic as UdonTypeExposureTree)
                List<Type> exposedTypes = BuildExposedTypeList();

                EditorUtility.DisplayProgressBar("Udon Exposure Export", $"Scanning {exposedTypes.Count} types...", 0.3f);

                // Step 2: Enumerate all members and check exposure
                List<MemberExposureInfo> allMembers = new List<MemberExposureInfo>();
                HashSet<string> whitelistedTypeNames = new HashSet<string>();

                int typeCount = 0;
                foreach (Type type in exposedTypes.OrderBy(t => t.FullName ?? t.Name))
                {
                    typeCount++;
                    if (typeCount % 50 == 0)
                        EditorUtility.DisplayProgressBar("Udon Exposure Export",
                            $"Scanning type {typeCount}/{exposedTypes.Count}: {type.Name}",
                            0.3f + 0.5f * typeCount / exposedTypes.Count);

                    string ns = type.Namespace ?? "(global)";
                    string typeName = type.Name;
                    string typeKind = GetTypeKind(type);

                    // Check if the type itself is recognized by Udon
                    string udonTypeName = CompilerUdonInterfaceProxy.GetUdonTypeName(type);
                    bool typeRecognized = UdonEditorManager.Instance.GetTypeFromTypeString(udonTypeName) != null;
                    if (typeRecognized)
                        whitelistedTypeNames.Add($"{ns}.{typeName}");

                    if (type.IsEnum)
                        continue; // Enum members are handled as the type itself

                    BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                    // Constructors
                    foreach (ConstructorInfo ctor in type.GetConstructors(flags))
                    {
                        if (ctor.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                        TryAddMethodMember(allMembers, ns, typeName, typeKind, ctor);
                    }

                    // Fields
                    foreach (FieldInfo field in type.GetFields(flags))
                    {
                        if (field.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                        if (field.DeclaringType?.FullName == null) continue;
                        TryAddFieldMember(allMembers, ns, typeName, typeKind, field);
                    }

                    // Properties
                    foreach (PropertyInfo prop in type.GetProperties(flags))
                    {
                        if (prop.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                        MethodInfo getter = prop.GetGetMethod();
                        if (getter == null || !getter.IsPublic) continue;
                        TryAddPropertyMember(allMembers, ns, typeName, typeKind, prop);
                    }

                    // Methods
                    foreach (MethodInfo method in type.GetMethods(flags))
                    {
                        if (method.GetCustomAttribute<ObsoleteAttribute>() != null) continue;
                        if (method.IsSpecialName && !method.Name.StartsWith("op_")) continue;
                        TryAddMethodMember(allMembers, ns, typeName, typeKind, method);
                    }
                }

                EditorUtility.DisplayProgressBar("Udon Exposure Export", "Writing files...", 0.85f);

                // Step 3: Write output files
                string projectRoot = Path.GetDirectoryName(Application.dataPath);

                var whitelisted = allMembers.Where(m => m.Exposed).ToList();
                var blacklisted = allMembers.Where(m => !m.Exposed).ToList();

                WriteExposureFile(Path.Combine(projectRoot, "udon_whitelisted.txt"), whitelisted,
                    "UDON WHITELISTED (EXPOSED) APIs",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Total: {whitelisted.Count} members across {whitelisted.Select(m => m.Namespace + "." + m.TypeName).Distinct().Count()} types");

                WriteExposureFile(Path.Combine(projectRoot, "udon_blacklisted.txt"), blacklisted,
                    "UDON BLACKLISTED (UNEXPOSED) APIs",
                    $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Total: {blacklisted.Count} members across {blacklisted.Select(m => m.Namespace + "." + m.TypeName).Distinct().Count()} types",
                    "NOTE: Only types that have at least one exposed member are listed here.");

                // Write types file
                WriteTypesFile(Path.Combine(projectRoot, "udon_whitelisted_types.txt"), whitelistedTypeNames);

                EditorUtility.ClearProgressBar();

                Debug.Log($"[UdonExposureExporter] Export complete!\n" +
                          $"  Whitelisted: {whitelisted.Count} members\n" +
                          $"  Blacklisted: {blacklisted.Count} members\n" +
                          $"  Recognized types: {whitelistedTypeNames.Count}\n" +
                          $"  Files written to: {projectRoot}");

                EditorUtility.RevealInFinder(Path.Combine(projectRoot, "udon_whitelisted.txt"));
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[UdonExposureExporter] Export failed: {e}");
            }
        }

        private static List<Type> BuildExposedTypeList()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            HashSet<Type> exposedTypeSet = new HashSet<Type>();

            foreach (Assembly assembly in assemblies)
            {
                if (assembly.FullName.Contains("UdonSharp") ||
                    assembly.FullName.Contains("CodeAnalysis"))
                    continue;

                Type[] assemblyTypes;
                try { assemblyTypes = assembly.GetTypes(); }
                catch { continue; }

                foreach (Type type in assemblyTypes)
                {
                    if (type.IsByRef) continue;

                    try
                    {
                        string typeName = CompilerUdonInterfaceProxy.GetUdonTypeName(type);
                        if (UdonEditorManager.Instance.GetTypeFromTypeString(typeName) != null)
                        {
                            exposedTypeSet.Add(type);
                            if (!type.IsGenericType && !type.IsGenericTypeDefinition)
                            {
                                try { exposedTypeSet.Add(type.MakeArrayType()); } catch { }
                            }
                        }

                        MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                        foreach (MethodInfo method in methods)
                        {
                            try
                            {
                                if (CompilerUdonInterfaceProxy.IsExposedToUdon(CompilerUdonInterfaceProxy.GetUdonMethodName(method)))
                                {
                                    exposedTypeSet.Add(method.DeclaringType);
                                }
                            }
                            catch { }
                        }

                        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                        {
                            MethodInfo getter = property.GetGetMethod();
                            if (getter == null) continue;
                            try
                            {
                                if (CompilerUdonInterfaceProxy.IsExposedToUdon(CompilerUdonInterfaceProxy.GetUdonMethodName(getter)))
                                {
                                    exposedTypeSet.Add(property.DeclaringType);
                                }
                            }
                            catch { }
                        }

                        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (field.DeclaringType?.FullName == null) continue;
                            try
                            {
                                if (CompilerUdonInterfaceProxy.IsExposedToUdon(
                                    CompilerUdonInterfaceProxy.GetUdonAccessorName(field, "Get")))
                                {
                                    exposedTypeSet.Add(field.DeclaringType);
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }

            exposedTypeSet.RemoveWhere(t => t.Name == "T" || t.Name == "T[]");
            return exposedTypeSet.ToList();
        }

        private static void TryAddMethodMember(List<MemberExposureInfo> list, string ns, string typeName, string typeKind, MethodBase method)
        {
            try
            {
                string udonName = CompilerUdonInterfaceProxy.GetUdonMethodName(method);
                bool exposed = CompilerUdonInterfaceProxy.IsExposedToUdon(udonName);
                bool isStatic = method.IsStatic;
                string memberKind = method.IsConstructor ? "Constructor" : "Method";
                string sig = FormatMethodSignature(method);

                list.Add(new MemberExposureInfo
                {
                    Namespace = ns,
                    TypeName = typeName,
                    TypeKind = typeKind,
                    MemberKind = memberKind,
                    IsStatic = isStatic,
                    Signature = sig,
                    UdonName = udonName,
                    Exposed = exposed,
                });
            }
            catch { }
        }

        private static void TryAddFieldMember(List<MemberExposureInfo> list, string ns, string typeName, string typeKind, FieldInfo field)
        {
            try
            {
                string getAccessor = CompilerUdonInterfaceProxy.GetUdonAccessorName(field, "Get");
                bool exposed = CompilerUdonInterfaceProxy.IsExposedToUdon(getAccessor);

                list.Add(new MemberExposureInfo
                {
                    Namespace = ns,
                    TypeName = typeName,
                    TypeKind = typeKind,
                    MemberKind = "Field",
                    IsStatic = field.IsStatic,
                    Signature = $"{PrettyTypeName(field.FieldType)} {field.Name}",
                    UdonName = getAccessor,
                    Exposed = exposed,
                });
            }
            catch { }
        }

        private static void TryAddPropertyMember(List<MemberExposureInfo> list, string ns, string typeName, string typeKind, PropertyInfo prop)
        {
            MethodInfo getter = prop.GetGetMethod();
            if (getter == null) return;

            try
            {
                string udonName = CompilerUdonInterfaceProxy.GetUdonMethodName(getter);
                bool exposed = CompilerUdonInterfaceProxy.IsExposedToUdon(udonName);

                // Also check setter
                string setterInfo = "";
                MethodInfo setter = prop.GetSetMethod();
                if (setter != null)
                {
                    try
                    {
                        string setUdon = CompilerUdonInterfaceProxy.GetUdonMethodName(setter);
                        bool setExposed = CompilerUdonInterfaceProxy.IsExposedToUdon(setUdon);
                        setterInfo = setExposed ? " { get; set; }" : " { get; }  [setter blacklisted]";
                    }
                    catch
                    {
                        setterInfo = " { get; }";
                    }
                }
                else
                {
                    setterInfo = " { get; }";
                }

                list.Add(new MemberExposureInfo
                {
                    Namespace = ns,
                    TypeName = typeName,
                    TypeKind = typeKind,
                    MemberKind = "Property",
                    IsStatic = getter.IsStatic,
                    Signature = $"{PrettyTypeName(prop.PropertyType)} {prop.Name}{setterInfo}",
                    UdonName = udonName,
                    Exposed = exposed,
                });
            }
            catch { }
        }

        private static string GetTypeKind(Type type)
        {
            if (type.IsEnum) return "enum";
            if (type.IsValueType) return "struct";
            if (type.IsArray) return "array";
            if (type.IsInterface) return "interface";
            return "class";
        }

        private static string FormatMethodSignature(MethodBase method)
        {
            string returnType = method is MethodInfo mi ? PrettyTypeName(mi.ReturnType) : "";
            string parameters = string.Join(", ",
                method.GetParameters().Select(p => $"{PrettyTypeName(p.ParameterType)} {p.Name}"));

            if (method.IsConstructor)
                return $"new({parameters})";
            return $"{returnType} {method.Name}({parameters})";
        }

        private static string PrettyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(long)) return "long";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(short)) return "short";
            if (type == typeof(uint)) return "uint";
            if (type == typeof(ulong)) return "ulong";
            if (type == typeof(char)) return "char";
            if (type == typeof(object)) return "object";
            if (type.IsArray) return PrettyTypeName(type.GetElementType()) + "[]";
            if (type.IsByRef) return "ref " + PrettyTypeName(type.GetElementType());
            if (type.IsGenericType)
            {
                string baseName = type.Name.Split('`')[0];
                string args = string.Join(", ", type.GetGenericArguments().Select(PrettyTypeName));
                return $"{baseName}<{args}>";
            }
            return type.Name;
        }

        private static void WriteExposureFile(string path, List<MemberExposureInfo> members, string title, params string[] headerLines)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"  {title}");
            foreach (string line in headerLines)
                sb.AppendLine($"  {line}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            // Group by namespace, then by type
            var byNamespace = members
                .GroupBy(m => m.Namespace)
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                sb.AppendLine(new string('-', 70));
                sb.AppendLine($"  NAMESPACE: {nsGroup.Key}");
                sb.AppendLine(new string('-', 70));

                var byType = nsGroup
                    .GroupBy(m => new { m.TypeName, m.TypeKind })
                    .OrderBy(g => g.Key.TypeName);

                foreach (var typeGroup in byType)
                {
                    sb.AppendLine();
                    sb.AppendLine($"  [{typeGroup.Key.TypeKind}] {nsGroup.Key}.{typeGroup.Key.TypeName}");
                    sb.AppendLine($"  {new string('~', Math.Min(60, typeGroup.Key.TypeName.Length + nsGroup.Key.Length + 12))}");

                    foreach (var member in typeGroup.OrderBy(m => m.MemberKind).ThenBy(m => m.Signature))
                    {
                        string staticTag = member.IsStatic ? "static " : "";
                        sb.AppendLine($"    [{member.MemberKind}] {staticTag}{member.Signature}");
                        sb.AppendLine($"             Udon: {member.UdonName}");
                    }
                }
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void WriteTypesFile(string path, HashSet<string> typeNames)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine("  UDON WHITELISTED TYPES");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Total: {typeNames.Count} types recognized by the Udon VM");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            foreach (string typeName in typeNames.OrderBy(t => t))
            {
                sb.AppendLine($"  {typeName}");
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}
#endif
