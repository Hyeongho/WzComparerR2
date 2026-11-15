using System.Runtime.InteropServices;
using WzComparerR2.WzLib;

namespace WzNativeLib;

public static unsafe partial class WzExports
{
    // 1은 직접 PNG, 2는 이미지 링크, 3은 이름이 역슬래시인 Canvas 경로도 지원한다.
    [UnmanagedCallersOnly(EntryPoint = "wz_canvas_api_version")]
    public static int WzCanvasApiVersion()
    {
        return 3;
    }

    // UI Canvas는 1x1 참조 PNG인 경우가 있다. 픽셀과 배치 메타데이터의 원본을 구분한다.
    private static Wz_Node? FindCanvasNode(Wz_Node? root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path))
        {
            return null;
        }
        // StatusBar의 숫자 구분자는 노드 이름 자체가 "\\"이다.
        // 정규화 전에 마지막 구분자 + 실제 이름을 보존한다. _outlink에도 같은 규칙을 적용한다.
        if (path.EndsWith("/\\", StringComparison.Ordinal) || path.EndsWith("\\\\", StringComparison.Ordinal))
        {
            Wz_Node? parent = FindCanvasNode(root, path.Substring(0, path.Length - 2));
            return parent?.Nodes["\\"];
        }
        string normalized = path.Replace('/', '\\').Trim('\\');
        Wz_Node? node = root.FindNodeByPath(normalized, true);
        if (node == null && normalized.StartsWith("UI\\", StringComparison.OrdinalIgnoreCase))
        {
            // UI.wz 또는 UI 폴더 자체를 연 루트에도 같은 UI 경로를 사용할 수 있다.
            node = root.FindNodeByPath(normalized.Substring(3), true);
        }
        return node;
    }

    private static Wz_Node? ResolveCanvasUol(Wz_Node? root, Wz_Node? node)
    {
        for (int depth = 0; node != null && depth < 32; depth++)
        {
            if (node.Value is not Wz_Uol uol)
            {
                return node;
            }
            if (string.IsNullOrEmpty(uol.Uol))
            {
                return null;
            }
            node = uol.Uol.StartsWith('/') ? FindCanvasNode(root, uol.Uol) : uol.HandleUol(node);
        }
        return null;
    }

    private static Wz_Png? ResolveCanvasPixels(Wz_Node? root, Wz_Node? original)
    {
        Wz_Node? node = original;
        for (int depth = 0; depth < 32; depth++)
        {
            node = ResolveCanvasUol(root, node);
            if (node == null)
            {
                return null;
            }
            string? source = node.Nodes["source"].GetValueEx<string>(null);
            string? inlink = node.Nodes["_inlink"].GetValueEx<string>(null);
            string? outlink = node.Nodes["_outlink"].GetValueEx<string>(null);
            if (!string.IsNullOrEmpty(source))
            {
                node = FindCanvasNode(root, source);
            }
            else if (!string.IsNullOrEmpty(inlink))
            {
                Wz_Image? image = node.GetNodeWzImage();
                node = FindCanvasNode(image?.Node, inlink);
            }
            else if (!string.IsNullOrEmpty(outlink))
            {
                node = FindCanvasNode(root, outlink);
            }
            else
            {
                return node.Value as Wz_Png;
            }
        }
        // 순환 UOL/이미지 링크는 참조용 1x1 PNG로 대체하지 않고 실패로 반환한다.
        return null;
    }
}
