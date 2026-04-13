// WzExports.cs — WzComparerR2.WzLib Native AOT 래퍼
//
// 빌드:
//   dotnet publish -r win-x64 -p:NativeLib=Shared -c Release
//   → bin\Release\net8.0\win-x64\publish\WzNativeLib.dll
//
// 반환 형식 (UTF-8):
//   첫 줄: "OK" 또는 "ERR\t<메시지>"
//   이후 각 줄: "<depth>\t<type>\t<name>"
//     depth : 0부터 시작하는 정수 (0 = 최상위 자식)
//     type  : 'I' (img) 또는 'D' (directory)
//     name  : 노드 이름

using System.Runtime.InteropServices;
using System.Text;
using WzComparerR2.WzLib;

namespace WzNativeLib;

public static unsafe class WzExports
{
    // ── wz_open ─────────────────────────────────────────────────────────────
    // MainForm.openWz() 동일 로직 — 파일 포맷을 자동 감지해서 로드
    //
    // pathUtf8: UTF-8 경로
    //   - *.ms / *.mn          → LoadMsFile
    //   - KMST1125 Base.wz     → LoadKMST1125DataWz (+ Packs/*.ms 자동 로드)
    //   - 그 외 *.wz           → Load(path, true)
    //
    // 반환: 트리 문자열 (wz_free 로 해제 필요)
    [UnmanagedCallersOnly(EntryPoint = "wz_open")]
    public static IntPtr WzOpen(IntPtr pathUtf8)
    {
        try
        {
            string path = Marshal.PtrToStringUTF8(pathUtf8)
                          ?? throw new ArgumentNullException("path");

            var wz = new Wz_Structure();
            string ext = Path.GetExtension(path);

            if (string.Equals(ext, ".ms", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".mn", StringComparison.OrdinalIgnoreCase))
            {
                wz.LoadMsFile(path);
            }
            else if (wz.IsKMST1125WzFormat(path))
            {
                wz.LoadKMST1125DataWz(path);

                // Base.wz 옆 Packs 폴더의 .ms/.mn 파일도 로드 (MainForm.openWz 동일)
                if (string.Equals(Path.GetFileName(path), "Base.wz", StringComparison.OrdinalIgnoreCase))
                {
                    string? dataDir = Path.GetDirectoryName(Path.GetDirectoryName(path));
                    if (dataDir != null)
                    {
                        string packsDir = Path.Combine(dataDir, "Packs");
                        if (Directory.Exists(packsDir))
                        {
                            foreach (string msFile in Directory.GetFiles(packsDir, "*.ms")
                                .Concat(Directory.GetFiles(packsDir, "*.mn")))
                            {
                                wz.LoadMsFile(msFile);
                            }
                        }
                    }
                }
            }
            else
            {
                wz.Load(path, true);
            }

            var sb = new StringBuilder(65536);
            sb.AppendLine("OK");

            if (wz.WzNode != null)
                TraverseNode(wz.WzNode, -1, sb);

            return MarshalUtf8(sb.ToString());
        }
        catch (Exception ex)
        {
            return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
        }
    }

    // ── wz_load_folder ──────────────────────────────────────────────────────
    // folderPathUtf8: UTF-8 경로 (예: C:\Nexon\Maple\Data\Skill)
    // 반환: 트리 문자열 (wz_free 로 해제 필요)
    [UnmanagedCallersOnly(EntryPoint = "wz_load_folder")]
    public static IntPtr WzLoadFolder(IntPtr folderPathUtf8)
    {
        try
        {
            string path = Marshal.PtrToStringUTF8(folderPathUtf8)
                          ?? throw new ArgumentNullException("path");

            var sb = new StringBuilder(65536);
            sb.AppendLine("OK");

            var structure = new Wz_Structure();
            Wz_Node? rootNode = null;
            structure.LoadWzFolder(path, ref rootNode, false);

            if (rootNode != null)
                TraverseNode(rootNode, -1, sb);

            return MarshalUtf8(sb.ToString());
        }
        catch (Exception ex)
        {
            return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
        }
    }

    // ── wz_load_file ────────────────────────────────────────────────────────
    // filePathUtf8: UTF-8 경로 (예: C:\Nexon\Maple\Data\Base\Base.wz)
    // 반환: 트리 문자열 (wz_free 로 해제 필요)
    [UnmanagedCallersOnly(EntryPoint = "wz_load_file")]
    public static IntPtr WzLoadFile(IntPtr filePathUtf8)
    {
        try
        {
            string path = Marshal.PtrToStringUTF8(filePathUtf8)
                          ?? throw new ArgumentNullException("path");

            var sb = new StringBuilder(65536);
            sb.AppendLine("OK");

            var structure = new Wz_Structure();
            structure.Load(path, false);

            if (structure.WzNode != null)
                TraverseNode(structure.WzNode, -1, sb);

            return MarshalUtf8(sb.ToString());
        }
        catch (Exception ex)
        {
            return MarshalUtf8("ERR\t" + ex.Message.Replace('\n', ' '));
        }
    }

    // ── wz_free ─────────────────────────────────────────────────────────────
    // wz_open / wz_load_folder / wz_load_file 가 반환한 포인터를 해제
    [UnmanagedCallersOnly(EntryPoint = "wz_free")]
    public static void WzFree(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            Marshal.FreeCoTaskMem(ptr);
    }

    // ── 내부 헬퍼 ────────────────────────────────────────────────────────────

    private static void TraverseNode(Wz_Node node, int depth, StringBuilder sb)
    {
        if (depth >= 0)
        {
            char type = node.Value is Wz_Image ? 'I' : 'D';
            sb.Append(depth);
            sb.Append('\t');
            sb.Append(type);
            sb.Append('\t');
            sb.AppendLine(node.Text);
        }

        foreach (Wz_Node child in node.Nodes)
            TraverseNode(child, depth + 1, sb);
    }

    private static IntPtr MarshalUtf8(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        IntPtr ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }
}
