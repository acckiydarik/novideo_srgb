using System.Drawing;
using System.Windows.Forms;

namespace novideo_srgb
{
    public static class TopMostMessageBox
    {
        public static void Show(string text)
        {
            using (var owner = new Form
            {
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2000, -2000),
                Size = Size.Empty,
                ShowInTaskbar = false
            })
            {
                owner.Show();
                MessageBox.Show(owner, text);
            }
        }
    }
}
