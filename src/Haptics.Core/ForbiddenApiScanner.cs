using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Haptics.Core;

public sealed record ApiViolation(string Assembly, string Kind, string Name);

/// <summary>
/// Static check (SPEC §10.3 / EXP-081): production assemblies must not import APIs that move the pointer,
/// synthesize input, steal focus, install hooks, inject into other processes, change system mouse settings,
/// or drive foreign UI through automation patterns. Inspects metadata only; never loads the assembly.
/// </summary>
public static class ForbiddenApiScanner
{
    public static readonly IReadOnlySet<string> ForbiddenImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SendInput", "mouse_event", "keybd_event", "SetCursorPos", "SetPhysicalCursorPos", "ClipCursor", "BlockInput",
        "SetForegroundWindow", "SetFocus", "SetActiveWindow", "SetCapture", "AttachThreadInput", "AllowSetForegroundWindow",
        "SetWindowsHookEx", "SetWindowsHookExA", "SetWindowsHookExW", "CreateRemoteThread", "CreateRemoteThreadEx",
        "WriteProcessMemory", "VirtualAllocEx", "QueueUserAPC", "NtCreateThreadEx", "SystemParametersInfo",
        "SystemParametersInfoA", "SystemParametersInfoW", "SwapMouseButton", "InjectTouchInput", "InjectSyntheticPointerInput",
        "CreateSyntheticPointerDevice", "RegisterHotKey", "GetAsyncKeyState", "GetKeyboardState",
    };

    /// <summary>Types whose mere use implies controlling or reading content from foreign UI.</summary>
    public static readonly IReadOnlySet<string> ForbiddenTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Windows.Automation.InvokePattern",
        "System.Windows.Automation.TogglePattern",
        "System.Windows.Automation.ValuePattern",
        "System.Windows.Automation.TextPattern",
        "System.Windows.Automation.ExpandCollapsePattern",
        "System.Windows.Automation.WindowPattern",
        "System.Windows.Automation.TransformPattern",
        "System.Windows.Forms.SendKeys",
        "Windows.UI.Input.Preview.Injection.InputInjector",
    };

    /// <summary>Patterns we may *read* state from, but whose action methods must never be called.</summary>
    public static readonly IReadOnlySet<string> ForbiddenMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Windows.Automation.SelectionItemPattern.Select",
        "System.Windows.Automation.SelectionItemPattern.AddToSelection",
        "System.Windows.Automation.SelectionItemPattern.RemoveFromSelection",
        "System.Windows.Automation.RangeValuePattern.SetValue",
        "System.Windows.Forms.Cursor.set_Position",
    };

    public static IReadOnlyList<ApiViolation> Scan(string assemblyPath)
    {
        var name = Path.GetFileName(assemblyPath);
        var list = new List<ApiViolation>();
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
        {
            return list;
        }

        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(h);
            var import = def.GetImport();
            if (import.Name.IsNil)
            {
                continue;
            }

            var entry = md.GetString(import.Name);
            if (string.IsNullOrEmpty(entry))
            {
                entry = md.GetString(def.Name);
            }

            if (ForbiddenImports.Contains(entry))
            {
                list.Add(new ApiViolation(name, "pinvoke", entry));
            }
        }

        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            var full = $"{md.GetString(tr.Namespace)}.{md.GetString(tr.Name)}";
            if (ForbiddenTypes.Contains(full))
            {
                list.Add(new ApiViolation(name, "type-reference", full));
            }
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var member = md.GetString(mr.Name);
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                var full = $"{md.GetString(tr.Namespace)}.{md.GetString(tr.Name)}.{member}";
                if (ForbiddenMembers.Contains(full))
                {
                    list.Add(new ApiViolation(name, "member-reference", full));
                }
            }
        }

        return list;
    }
}
