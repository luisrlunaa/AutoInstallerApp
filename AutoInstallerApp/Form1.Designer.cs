using System.Drawing;
using System.Windows.Forms;

namespace AutoInstallerApp
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            txtFolder = new TextBox();
            btnStart = new Button();
            listLog = new ListBox();
            progressBar = new ProgressBar();
            btnOpenLog = new Button();
            label1 = new Label();
            SuspendLayout();
            // 
            // txtFolder
            // 
            txtFolder.Location = new Point(12, 40);
            txtFolder.Name = "txtFolder";
            txtFolder.Size = new Size(448, 23);
            txtFolder.TabIndex = 5;
            // 
            // btnStart
            // 
            btnStart.Location = new Point(12, 73);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(448, 30);
            btnStart.TabIndex = 3;
            btnStart.Text = "Start Installation";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // listLog
            // 
            listLog.Location = new Point(12, 148);
            listLog.Name = "listLog";
            listLog.Size = new Size(448, 229);
            listLog.TabIndex = 1;
            // 
            // progressBar
            // 
            progressBar.Location = new Point(12, 113);
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(448, 25);
            progressBar.TabIndex = 2;
            // 
            // btnOpenLog
            // 
            btnOpenLog.Location = new Point(12, 388);
            btnOpenLog.Name = "btnOpenLog";
            btnOpenLog.Size = new Size(448, 30);
            btnOpenLog.TabIndex = 0;
            btnOpenLog.Text = "Open log file";
            btnOpenLog.UseVisualStyleBackColor = true;
            btnOpenLog.Click += btnOpenLog_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Segoe UI", 14.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.Location = new Point(12, 9);
            label1.Name = "label1";
            label1.Size = new Size(122, 25);
            label1.TabIndex = 6;
            label1.Text = "Folder Root:";
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(472, 425);
            Controls.Add(label1);
            Controls.Add(btnOpenLog);
            Controls.Add(listLog);
            Controls.Add(progressBar);
            Controls.Add(btnStart);
            Controls.Add(txtFolder);
            Name = "Form1";
            Text = "Auto Installer";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TextBox txtFolder;
        private Button btnStart;
        private ListBox listLog;
        private ProgressBar progressBar;
        private Button btnOpenLog;
        private Label label1;
    }
}
