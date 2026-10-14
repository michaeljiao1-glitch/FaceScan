using System.Drawing;
using System.Windows.Forms;

namespace FaceScan
{
    /// <summary>Small modal dialog asking for one line of text.</summary>
    internal static class PromptDialog
    {
        public static string Ask(IWin32Window owner, string title, string prompt)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ClientSize = new Size(380, 150);

                var label = new Label { Text = prompt, Location = new Point(12, 12), Size = new Size(356, 40) };
                var input = new TextBox { Location = new Point(12, 60), Size = new Size(356, 24) };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(212, 106), Size = new Size(75, 28) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(293, 106), Size = new Size(75, 28) };
                form.Controls.AddRange(new Control[] { label, input, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
            }
        }
    }
}
