using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XiaoheiheFloat
{
    /// <summary>
    /// 状态条上的缩放滑块。不用 TrackBar，自己画、自己算点击位置，
    /// 点在轨道任意位置都能直接跳到对应比例，拖动也全程跟随。
    /// </summary>
    internal sealed class ZoomSlider : Control
    {
        private static readonly Color ColTrack = Color.FromArgb(255, 62, 70, 86);
        private static readonly Color ColFill = Color.FromArgb(255, 74, 158, 255);
        private static readonly Color ColThumb = Color.FromArgb(255, 226, 232, 240);

        private int _min = 100;
        private int _max = 300;
        private int _value = 100;
        private bool _dragging;

        public event EventHandler ValueChanged;

        public ZoomSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = false;
            Cursor = Cursors.Hand;
            // 背景色由父级状态条决定，这里直接用它的颜色，避免"透明背景"不被支持
            BackColor = Color.FromArgb(255, 26, 30, 38);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public int Minimum { get { return _min; } set { _min = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public int Maximum { get { return _max; } set { _max = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public double ZoomFromValue()
        {
            return Math.Max(0.3, Math.Min(3.0, _value / 100.0));
        }

        public void ValueFromZoom(double zoom)
        {
            Value = (int)Math.Round(Math.Max(0.3, Math.Min(3.0, zoom)) * 100);
        }

        private Rectangle TrackRect
        {
            get
            {
                int h = 4;
                return new Rectangle(Padding.Left + 6, (Height - h) / 2, Math.Max(1, Width - Padding.Left - Padding.Right - 12), h);
            }
        }

        private int ThumbX
        {
            get
            {
                Rectangle t = TrackRect;
                double ratio = (_value - _min) / (double)Math.Max(1, _max - _min);
                return t.Left + (int)Math.Round(t.Width * ratio);
            }
        }

        private void SetValueFromX(int x)
        {
            Rectangle t = TrackRect;
            double ratio = (x - t.Left) / (double)Math.Max(1, t.Width);
            ratio = Math.Max(0, Math.Min(1, ratio));
            Value = (int)Math.Round(_min + ratio * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            SetValueFromX(e.X);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetValueFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value = _value + (e.Delta > 0 ? 5 : -5);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Left: Value = _value - 5; e.Handled = true; break;
                case Keys.Right: Value = _value + 5; e.Handled = true; break;
                case Keys.PageDown: Value = _value - 25; e.Handled = true; break;
                case Keys.PageUp: Value = _value + 25; e.Handled = true; break;
                case Keys.Home: Value = _min; e.Handled = true; break;
                case Keys.End: Value = _max; e.Handled = true; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle t = TrackRect;
            using (SolidBrush b = new SolidBrush(ColTrack))
            using (GraphicsPath path = Rounded(t, t.Height / 2))
            {
                g.FillPath(b, path);
            }

            int thumbX = ThumbX;
            int fillW = Math.Max(0, thumbX - t.Left);
            if (fillW > 0)
            {
                using (SolidBrush b = new SolidBrush(ColFill))
                using (GraphicsPath path = Rounded(new Rectangle(t.Left, t.Top, fillW, t.Height), t.Height / 2))
                {
                    g.FillPath(b, path);
                }
            }

            int thumbSize = 11;
            Rectangle thumb = new Rectangle(thumbX - thumbSize / 2, (Height - thumbSize) / 2, thumbSize, thumbSize);
            using (SolidBrush b = new SolidBrush(_dragging ? ColFill : ColThumb))
            {
                g.FillEllipse(b, thumb);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= d || r.Height <= d)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
