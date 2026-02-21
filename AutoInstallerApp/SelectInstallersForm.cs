namespace AutoInstallerApp
{
    public class SelectInstallersForm : Form
    {
        private CheckedListBox chkList;
        private Button btnInstall;
        private Button btnCancel;
        public string[] SelectedFiles { get; private set; } = Array.Empty<string>();

        private class InstallerItem
        {
            public string Path { get; set; } = string.Empty;
            public string Name => System.IO.Path.GetFileName(Path);
            public override string ToString() => Name;
        }

        public SelectInstallersForm(string[] installers)
        {
            this.Text = "Select installers to run";
            this.Width = 600;
            this.Height = 400;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;

            chkList = new CheckedListBox();
            chkList.CheckOnClick = true;
            chkList.Dock = DockStyle.Top;
            chkList.Height = 280;

            foreach (var f in installers)
            {
                var it = new InstallerItem { Path = f };
                chkList.Items.Add(it, true);
            }

            btnInstall = new Button();
            btnInstall.Text = "Install Selected";
            btnInstall.Width = 140;
            btnInstall.Top = chkList.Bottom + 10;
            btnInstall.Left = this.ClientSize.Width / 2 - btnInstall.Width - 10;
            btnInstall.Anchor = AnchorStyles.Bottom;
            btnInstall.Click += (s, e) =>
            {
                var sel = chkList.CheckedItems.Cast<InstallerItem>().Select(i => i.Path).ToArray();
                SelectedFiles = sel;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Width = 80;
            btnCancel.Top = chkList.Bottom + 10;
            btnCancel.Left = this.ClientSize.Width / 2 + 10;
            btnCancel.Anchor = AnchorStyles.Bottom;
            btnCancel.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            var lbl = new Label();
            lbl.Text = "Uncheck any installers you do not want to run, then press 'Install Selected'.";
            lbl.AutoSize = false;
            lbl.Height = 30;
            lbl.Dock = DockStyle.Top;
            lbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            this.Controls.Add(btnCancel);
            this.Controls.Add(btnInstall);
            this.Controls.Add(chkList);
            this.Controls.Add(lbl);
        }
    }
}
