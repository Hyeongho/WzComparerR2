// BitmapOrigin.cs — WzComparerR2.Common/BitmapOrigin.cs의 로컬 사본.
//
// 원본을 그대로 링크하지 않고 사본을 둔 이유는 원본이 참조하는 몇몇 GUI 전용
// 확장 기능(예: XNA Texture2D 관련 오버로드가 있는 형제 파일들)까지 통째로
// 끌려올 위험을 피하려는 것 — 이 파일 자체가 실제로 쓰는 건
// `node.HandleFullUol(...)`/`node.GetLinkedSourceNode(...)` 확장 메서드
// (WzComparerR2.Common.Wz_NodeExtension2에 정의, 우리가 이것도 개별
// 링크했음)뿐이라 `using WzComparerR2.Common;`은 그대로 필요하다.
using System.Drawing;
using WzComparerR2.WzLib;
using WzComparerR2.Common;

namespace WzComparerR2
{
	public struct BitmapOrigin
	{
		public BitmapOrigin(Bitmap bitmap)
			: this(bitmap, new Point(0, 0))
		{
		}

		public BitmapOrigin(Bitmap bitmap, int x, int y)
			: this(bitmap, new Point(x, y))
		{
		}

		public BitmapOrigin(Bitmap bitmap, Point origin)
		{
			this.bitmap = bitmap;
			this.origin = origin;
		}

		private Bitmap bitmap;
		private Point origin;

		public Bitmap Bitmap
		{
			get { return bitmap; }
			set { bitmap = value; }
		}

		public Point Origin
		{
			get { return origin; }
			set { origin = value; }
		}

		public Point OpOrigin
		{
			get { return new Point(-origin.X, -origin.Y); }
		}

		public Rectangle Rectangle
		{
			get
			{
				if (this.bitmap == null)
					return new Rectangle(this.OpOrigin, new Size());
				else
					return new Rectangle(this.OpOrigin, this.bitmap.Size);
			}
		}

		public static BitmapOrigin CreateFromNode(Wz_Node node, GlobalFindNodeFunction findNode, Wz_File wzf = null)
		{
			BitmapOrigin bp = new BitmapOrigin();
			node = node.HandleFullUol(findNode, wzf);
			if (node == null)
			{
				return bp;
			}

			var linkNode = node.GetLinkedSourceNode(findNode, wzf);
			Wz_Png png = linkNode?.GetValue<Wz_Png>() ?? (Wz_Png)node.Value;

			bp.Bitmap = png?.ExtractPng();
			Wz_Node originNode = node.FindNodeByPath("origin");
			Wz_Vector vec = (originNode == null) ? null : originNode.GetValue<Wz_Vector>();
			bp.Origin = (vec == null) ? new Point() : new Point(vec.X, vec.Y);

			return bp;
		}
	}
}
