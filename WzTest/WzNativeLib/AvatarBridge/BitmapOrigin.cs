// BitmapOrigin.cs — WzComparerR2.Common/BitmapOrigin.cs의 로컬 사본.
//
// 원본을 그대로 링크하지 않고 사본을 둔 이유: 원본이 쓰는
// `using WzComparerR2.Common;`이 실제로는 아무 타입도 참조하지 않는데(같은
// 파일에서 실제로 쓰는 GlobalFindNodeFunction은 bare `WzComparerR2` 네임스페이스라
// using 없이도 보임), 우리 프로젝트는 WzComparerR2.Common 네임스페이스를 선언하는
// 다른 파일(GifFrame.cs 등)을 하나도 링크하지 않으므로 그 using 한 줄만으로
// 네임스페이스를 못 찾는 컴파일 에러가 날 수 있다. 그 한 줄만 뺀 것 외에는
// 원본과 동일.
using System.Drawing;
using WzComparerR2.WzLib;

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
