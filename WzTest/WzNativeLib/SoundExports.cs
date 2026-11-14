using System.Runtime.InteropServices;
using WzComparerR2.WzLib;

namespace WzNativeLib;

public static unsafe partial class WzExports
{
    // 반환 버퍼는 MP3 또는 RIFF/WAVE 파일 형식이다. 호출자는 wz_free로 해제한다.
    // PCM 디코딩과 실제 재생은 게임 엔진에서 수행하며 DLL은 사운드 바이트만 추출한다.
    [UnmanagedCallersOnly(EntryPoint = "wz_read_sound")]
    public static IntPtr WzReadSound(IntPtr wzPathUtf8, IntPtr nodePathUtf8, int* outLen)
    {
        if (outLen == null)
        {
            return IntPtr.Zero;
        }
        *outLen = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            string? wzPath = Marshal.PtrToStringUTF8(wzPathUtf8);
            string? nodePath = Marshal.PtrToStringUTF8(nodePathUtf8);
            if (string.IsNullOrWhiteSpace(wzPath) || string.IsNullOrWhiteSpace(nodePath))
            {
                return IntPtr.Zero;
            }
            Wz_Node? root = GetRoot(wzPath);
            Wz_Node? node = FindSoundNode(root, NormalizeSoundPath(nodePath));
            if (node?.Value is not Wz_Sound sound || sound.DataLength <= 0
                || sound.DataLength > 256 * 1024 * 1024 - 44
                || (sound.SoundType != Wz_SoundType.Mp3 && sound.SoundType != Wz_SoundType.Pcm))
            {
                return IntPtr.Zero;
            }

            // 원본 ExtractSound는 MP3는 그대로, PCM은 44바이트 WAV 헤더를 붙여 반환한다.
            byte[]? bytes = sound.ExtractSound();
            if (bytes == null || bytes.Length == 0)
            {
                return IntPtr.Zero;
            }
            buffer = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            *outLen = bytes.Length;
            return buffer;
        }
        catch
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
            *outLen = 0;
            return IntPtr.Zero;
        }
    }

    // 기존 NativeMapInfo의 크기를 바꾸지 않도록 별도 API로 BGM 경로를 반환한다.
    // 반환값은 null 종료 UTF-8이며 wz_free로 해제한다. BGM이 없으면 null이다.
    [UnmanagedCallersOnly(EntryPoint = "wz_map_read_bgm")]
    public static IntPtr WzMapReadBgm(IntPtr wzPathUtf8, IntPtr mapPathUtf8)
    {
        try
        {
            // 링크 맵의 배치와 달리 BGM은 요청한 맵의 info에서 읽는다.
            Wz_Node? info = FindMapNode(wzPathUtf8, mapPathUtf8, resolveLink: false)?.Nodes["info"];
            string? bgm = info?.Nodes["bgm"].GetValueEx<string>(null);
            string path = NormalizeSoundPath(bgm);
            return path.Length == 0 ? IntPtr.Zero : MarshalUtf8(path);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string NormalizeSoundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        string normalized = path.Replace('/', '\\').Trim('\\');
        if (normalized.StartsWith("Sound\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(6);
        }
        else if (normalized.StartsWith("Sound.wz\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(9);
        }
        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }
        // MapRender2처럼 Bgm00/곡이름의 첫 구간에만 .img를 보충한다.
        if (!parts[0].EndsWith(".img", StringComparison.OrdinalIgnoreCase))
        {
            parts[0] += ".img";
        }
        return "Sound\\" + string.Join("\\", parts);
    }

    private static Wz_Node? FindSoundNode(Wz_Node? root, string path)
    {
        if (root == null || path.Length == 0)
        {
            return null;
        }
        // Base.wz의 통합 루트와 Sound.wz를 직접 연 루트 모두 지원한다.
        Wz_Node? node = root.FindNodeByPath(path, true)
            ?? root.FindNodeByPath(path.Substring(6), true);
        for (int depth = 0; node != null && depth < 32; depth++)
        {
            if (node.Value is Wz_Sound)
            {
                return node;
            }
            if (node.Value is Wz_Uol uol)
            {
                // 절대 링크는 통합 루트에서, 상대 링크는 원본 WZ 규칙으로 해석한다.
                if (uol.Uol.StartsWith('/'))
                {
                    string absolutePath = NormalizeSoundPath(uol.Uol);
                    node = absolutePath.Length == 0 ? null
                        : root.FindNodeByPath(absolutePath, true)
                            ?? root.FindNodeByPath(absolutePath.Substring(6), true);
                }
                else
                {
                    node = uol.HandleUol(node);
                }
            }
            else if (node.Value == null)
            {
                // MapRender2의 multi BGM 기본 선택처럼 첫 사운드 또는 UOL을 사용한다.
                Wz_Node? firstSound = null;
                foreach (Wz_Node child in node.Nodes)
                {
                    if (child.Value is Wz_Sound || child.Value is Wz_Uol)
                    {
                        firstSound = child;
                        break;
                    }
                }
                node = firstSound;
            }
            else
            {
                return null;
            }
        }
        // 잘못된 순환 UOL이 있어도 무한히 탐색하지 않는다.
        return null;
    }
}
