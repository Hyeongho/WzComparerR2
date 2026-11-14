using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WzComparerR2;
using WzComparerR2.Common;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzNativeLib;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeSpineVertex
{
	public float X, Y, U, V;
	public float R, G, B, A;
	public int Page, Blend;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NativeSpineMeta
{
	public int PageCount, Version;
	public int BoundsX, BoundsY, BoundsW, BoundsH;
	public int HasFlowX, FlowX, HasFlowY, FlowY;
}

// 실제 Spine 런타임으로 본·메시·클리핑을 계산한다. 그래픽 장치는 생성하지 않는다.
internal sealed class NativeSpineScene : Spine.TextureLoader, Spine.V2.TextureLoader, IDisposable
{
	internal sealed class Page
	{
		public int Index, Width, Height;
		public byte[] Pixels = Array.Empty<byte>();
	}

	private readonly Wz_Node m_TextureRoot;
	private Spine.Atlas? m_AtlasV4;
	private Spine.V2.Atlas? m_AtlasV2;
	private Spine.Skeleton? m_SkeletonV4;
	private Spine.V2.Skeleton? m_SkeletonV2;
	private Spine.AnimationState? m_StateV4;
	private Spine.V2.AnimationState? m_StateV2;
	private readonly Spine.SkeletonClipping m_Clipping = new();
	private float[] m_WorldVertices = new float[8];
	private readonly float m_TimeScale;
	private static readonly int[] RegionTriangles = { 0, 1, 2, 2, 3, 0 };
	public List<Page> Pages { get; } = new();
	public List<NativeSpineVertex> Vertices { get; } = new();
	public NativeSpineMeta Meta;

	public static SpineDetectionResult Detect(Wz_Node? node)
	{
		node = node?.ResolveUol();
		if (node == null)
		{
			return SpineDetectionResult.Failed("Spine resource node not found.");
		}
		// MapRender2는 직접 skeleton/atlas 노드와 spine 이름이 붙은 디렉터리를 모두 지원한다.
		SpineDetectionResult result = SpineLoader.Detect(node);
		if (!result.Success && node.Nodes["spine"].GetValueEx<string>(null) is string name)
		{
			result = SpineLoader.Detect(node.Nodes[name + ".atlas"]);
		}
		return result;
	}

	public NativeSpineScene(Wz_Node node, SpineDetectionResult detection, string? animation, float timeScale)
	{
		m_TextureRoot = detection.SourceNode.ParentNode;
		m_TimeScale = float.IsFinite(timeScale) && timeScale >= 0 ? timeScale : 1;
		bool loop = node.Nodes["repeat"].GetValueEx(1) != 0;
		try
		{
			// WzComparer와 같은 화면 좌표계(Y가 아래로 증가)를 사용한다.
			Spine.Bone.yDown = true;
			Spine.V2.Bone.yDown = true;
			if (detection.Version == SpineVersion.V4)
			{
				m_AtlasV4 = new Spine.Atlas(new StringReader((string)detection.ResolvedAtlasNode.Value), "", this);
				Spine.SkeletonData data = SpineLoader.LoadSkeletonV4(detection, this, m_AtlasV4)
					?? throw new InvalidDataException("Spine 4 skeleton could not be loaded.");
				m_SkeletonV4 = new Spine.Skeleton(data);
				m_StateV4 = new Spine.AnimationState(new Spine.AnimationStateData(data));
				// MapRender2처럼 이름이 없거나 찾지 못하면 setup pose를 유지한다.
				if (!string.IsNullOrEmpty(animation) && data.FindAnimation(animation) != null)
				{
					m_StateV4.SetAnimation(0, animation, loop);
				}
				m_SkeletonV4.SetToSetupPose();
			}
			else if (detection.Version == SpineVersion.V2)
			{
				m_AtlasV2 = new Spine.V2.Atlas(new StringReader((string)detection.ResolvedAtlasNode.Value), "", this);
				Spine.V2.SkeletonData data = SpineLoader.LoadSkeletonV2(detection, this, m_AtlasV2)
					?? throw new InvalidDataException("Spine 2 skeleton could not be loaded.");
				m_SkeletonV2 = new Spine.V2.Skeleton(data);
				m_StateV2 = new Spine.V2.AnimationState(new Spine.V2.AnimationStateData(data));
				if (!string.IsNullOrEmpty(animation) && data.FindAnimation(animation) != null)
				{
					m_StateV2.SetAnimation(0, animation, loop);
				}
				m_SkeletonV2.SetToSetupPose();
			}
			else
			{
				throw new NotSupportedException("Only the existing Spine 2/4 runtimes are supported.");
			}
			Update(0);
			if (Pages.Count == 0)
			{
				throw new InvalidDataException("Spine atlas has no texture pages.");
			}
			Meta.PageCount = Pages.Count;
			Meta.Version = (int)detection.Version;
			Meta.HasFlowX = node.Nodes["flowX"] != null ? 1 : 0;
			Meta.FlowX = node.Nodes["flowX"].GetValueEx(0);
			Meta.HasFlowY = node.Nodes["flowY"] != null ? 1 : 0;
			Meta.FlowY = node.Nodes["flowY"].GetValueEx(0);
			Measure();
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	private Page LoadPage(string path)
	{
		Wz_Node? source = m_TextureRoot.FindNodeByPath(path.Replace('/', '\\'), true)?.ResolveUol();
		Wz_Node? linked = source?.GetLinkedSourceNode(PluginManager.FindWz) ?? source;
		if (linked?.Value is not Wz_Png png)
		{
			throw new InvalidDataException($"Spine atlas texture not found: {path}");
		}
		using Bitmap bitmap = png.ExtractPng() ?? throw new InvalidDataException($"Invalid atlas texture: {path}");
		Page page = new() { Index = Pages.Count, Width = bitmap.Width, Height = bitmap.Height };
		int rowBytes = checked(page.Width * 4);
		page.Pixels = new byte[checked(rowBytes * page.Height)];
		BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, page.Width, page.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			for (int y = 0; y < page.Height; y++)
			{
				Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), page.Pixels, y * rowBytes, rowBytes);
			}
		}
		finally
		{
			bitmap.UnlockBits(bits);
		}
		Pages.Add(page);
		return page;
	}

	public void Load(Spine.AtlasPage page, string path)
	{
		Page texture = LoadPage(path);
		page.rendererObject = texture;
		page.width = texture.Width;
		page.height = texture.Height;
	}

	public void Load(Spine.V2.AtlasPage page, string path)
	{
		Page texture = LoadPage(path);
		page.rendererObject = texture;
		page.width = texture.Width;
		page.height = texture.Height;
	}

	public void Unload(object texture)
	{
		// 관리 배열의 수명은 이 인스턴스가 소유한다. 네이티브 복사본은 wz_free로 해제한다.
	}

	private void EnsureWorldVertices(int length)
	{
		if (m_WorldVertices.Length < length)
		{
			m_WorldVertices = new float[length];
		}
	}

	private void Append(float[] positions, float[] uvs, int[] triangles, int count, Page page, int blend,
		float r, float g, float b, float a)
	{
		if (a <= 0)
		{
			return;
		}
		for (int i = 0; i < count; i++)
		{
			int vertex = checked(triangles[i] * 2);
			Vertices.Add(new NativeSpineVertex { X = positions[vertex], Y = positions[vertex + 1],
				U = uvs[vertex], V = uvs[vertex + 1], R = r, G = g, B = b, A = a, Page = page.Index, Blend = blend });
		}
	}

	public void Update(float deltaTime)
	{
		Vertices.Clear();
		float elapsed = deltaTime * m_TimeScale;
		if (!float.IsFinite(elapsed) || elapsed < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(deltaTime));
		}
		if (m_SkeletonV4 != null && m_StateV4 != null)
		{
			m_StateV4.Update(elapsed);
			m_StateV4.Apply(m_SkeletonV4);
			m_SkeletonV4.UpdateWorldTransform();
			BuildV4(m_SkeletonV4);
		}
		else if (m_SkeletonV2 != null && m_StateV2 != null)
		{
			m_StateV2.Update(elapsed);
			m_StateV2.Apply(m_SkeletonV2);
			m_SkeletonV2.UpdateWorldTransform();
			BuildV2(m_SkeletonV2);
		}
	}

	private void BuildV4(Spine.Skeleton skeleton)
	{
		try
		{
			for (int i = 0; i < skeleton.DrawOrder.Count; i++)
			{
				Spine.Slot slot = skeleton.DrawOrder.Items[i];
				if (!slot.Bone.Active)
				{
					m_Clipping.ClipEnd(slot);
					continue;
				}
				if (slot.Attachment is Spine.ClippingAttachment clip)
				{
					m_Clipping.ClipStart(slot, clip);
					continue;
				}
				float[] uvs;
				int[] triangles;
				int length;
				float r, g, b, a;
				Spine.AtlasRegion? region;
				if (slot.Attachment is Spine.RegionAttachment quad)
				{
					length = 8;
					quad.ComputeWorldVertices(slot, m_WorldVertices, 0, 2);
					uvs = quad.UVs;
					triangles = RegionTriangles;
					region = quad.Region as Spine.AtlasRegion;
					r = quad.R; g = quad.G; b = quad.B; a = quad.A;
				}
				else if (slot.Attachment is Spine.MeshAttachment mesh)
				{
					length = mesh.WorldVerticesLength;
					EnsureWorldVertices(length);
					mesh.ComputeWorldVertices(slot, 0, length, m_WorldVertices, 0, 2);
					uvs = mesh.UVs;
					triangles = mesh.Triangles;
					region = mesh.Region as Spine.AtlasRegion;
					r = mesh.R; g = mesh.G; b = mesh.B; a = mesh.A;
				}
				else
				{
					m_Clipping.ClipEnd(slot);
					continue;
				}
				if (region?.page.rendererObject is Page page)
				{
					r *= skeleton.R * slot.R; g *= skeleton.G * slot.G;
					b *= skeleton.B * slot.B; a *= skeleton.A * slot.A;
					if (m_Clipping.IsClipping)
					{
						m_Clipping.ClipTriangles(m_WorldVertices, length, triangles, triangles.Length, uvs);
						Append(m_Clipping.ClippedVertices.Items, m_Clipping.ClippedUVs.Items,
							m_Clipping.ClippedTriangles.Items, m_Clipping.ClippedTriangles.Count, page, (int)slot.Data.BlendMode, r, g, b, a);
					}
					else
					{
						Append(m_WorldVertices, uvs, triangles, triangles.Length, page, (int)slot.Data.BlendMode, r, g, b, a);
					}
				}
				m_Clipping.ClipEnd(slot);
			}
		}
		finally
		{
			m_Clipping.ClipEnd();
		}
	}

	private void BuildV2(Spine.V2.Skeleton skeleton)
	{
		for (int i = 0; i < skeleton.DrawOrder.Count; i++)
		{
			Spine.V2.Slot slot = skeleton.DrawOrder.Items[i];
			float[] uvs;
			int[] triangles;
			float r, g, b, a;
			Spine.V2.AtlasRegion? region;
			if (slot.Attachment is Spine.V2.RegionAttachment quad)
			{
				quad.ComputeWorldVertices(slot.Bone, m_WorldVertices);
				uvs = quad.UVs; triangles = RegionTriangles;
				region = quad.RendererObject as Spine.V2.AtlasRegion;
				r = quad.R; g = quad.G; b = quad.B; a = quad.A;
			}
			else if (slot.Attachment is Spine.V2.MeshAttachment mesh)
			{
				EnsureWorldVertices(mesh.Vertices.Length);
				mesh.ComputeWorldVertices(slot, m_WorldVertices);
				uvs = mesh.UVs; triangles = mesh.Triangles;
				region = mesh.RendererObject as Spine.V2.AtlasRegion;
				r = mesh.R; g = mesh.G; b = mesh.B; a = mesh.A;
			}
			else if (slot.Attachment is Spine.V2.SkinnedMeshAttachment weighted)
			{
				EnsureWorldVertices(weighted.UVs.Length);
				weighted.ComputeWorldVertices(slot, m_WorldVertices);
				uvs = weighted.UVs; triangles = weighted.Triangles;
				region = weighted.RendererObject as Spine.V2.AtlasRegion;
				r = weighted.R; g = weighted.G; b = weighted.B; a = weighted.A;
			}
			else
			{
				continue;
			}
			if (region?.page.rendererObject is Page page)
			{
				Append(m_WorldVertices, uvs, triangles, triangles.Length, page, (int)slot.Data.BlendMode,
					r * skeleton.R * slot.R, g * skeleton.G * slot.G, b * skeleton.B * slot.B, a * skeleton.A * slot.A);
			}
		}
	}

	private void Measure()
	{
		if (Vertices.Count == 0)
		{
			return;
		}
		float left = float.PositiveInfinity, top = float.PositiveInfinity;
		float right = float.NegativeInfinity, bottom = float.NegativeInfinity;
		foreach (NativeSpineVertex vertex in Vertices)
		{
			left = Math.Min(left, vertex.X); top = Math.Min(top, vertex.Y);
			right = Math.Max(right, vertex.X); bottom = Math.Max(bottom, vertex.Y);
		}
		Meta.BoundsX = (int)Math.Floor(left); Meta.BoundsY = (int)Math.Floor(top);
		Meta.BoundsW = (int)Math.Ceiling(right) - Meta.BoundsX;
		Meta.BoundsH = (int)Math.Ceiling(bottom) - Meta.BoundsY;
	}

	public void Dispose()
	{
		m_AtlasV4?.Dispose(); m_AtlasV4 = null;
		m_AtlasV2?.Dispose(); m_AtlasV2 = null;
		Pages.Clear();
		Vertices.Clear();
	}
}
