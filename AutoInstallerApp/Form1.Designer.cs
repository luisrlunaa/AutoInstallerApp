using AutoInstallerApp.Language;
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            txtFolder = new TextBox();
            chkInstallAll = new CheckBox();
            btnStart = new Button();
            listLog = new ListBox();
            progressBar = new ProgressBar();
            btnOpenLog = new Button();
            label1 = new Label();
            label2 = new Label();
            btnStop = new Button();
            pictureBox1 = new PictureBox();
            lblTimer = new Label();
            lblProgress = new Label();
            progressBarlbl = new Label();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            SuspendLayout();
            // 
            // txtFolder
            // 
            txtFolder.Location = new Point(12, 95);
            txtFolder.Name = "txtFolder";
            txtFolder.Size = new Size(430, 23);
            txtFolder.TabIndex = 5;
            // 
            // chkInstallAll
            // 
            chkInstallAll.AutoSize = true;
            chkInstallAll.Location = new Point(459, 143);
            chkInstallAll.Name = "chkInstallAll";
            chkInstallAll.Size = new Size(15, 14);
            chkInstallAll.TabIndex = 6;
            chkInstallAll.UseVisualStyleBackColor = true;
            // 
            // btnStart
            // 
            btnStart.Location = new Point(12, 132);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(430, 30);
            btnStart.TabIndex = 3;
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // listLog
            // 
            listLog.Location = new Point(12, 203);
            listLog.Name = "listLog";
            listLog.Size = new Size(568, 229);
            listLog.TabIndex = 1;
            // 
            // progressBar
            // 
            progressBar.Location = new Point(12, 168);
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(518, 25);
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.TabIndex = 2;
            // 
            // btnOpenLog
            // 
            btnOpenLog.Location = new Point(12, 443);
            btnOpenLog.Name = "btnOpenLog";
            btnOpenLog.Size = new Size(172, 30);
            btnOpenLog.TabIndex = 0;
            btnOpenLog.UseVisualStyleBackColor = true;
            btnOpenLog.Click += btnOpenLog_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Segoe UI", 14.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            label1.Location = new Point(12, 67);
            label1.Name = "label1";
            label1.Size = new Size(0, 25);
            label1.TabIndex = 6;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 8.25F, FontStyle.Italic, GraphicsUnit.Point, 0);
            label2.Location = new Point(509, 460);
            label2.Name = "label2";
            label2.Size = new Size(70, 13);
            label2.TabIndex = 7;
            label2.Text = "LunasSystems";
            // 
            // btnStop
            // 
            btnStop.Location = new Point(12, 132);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(430, 30);
            btnStop.TabIndex = 8;
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // pictureBox1
            // 
            pictureBox1.Image = (Image)resources.GetObject("pictureBox1.Image");
            pictureBox1.Location = new Point(459, 9);
            pictureBox1.Name = "pictureBox1";
            pictureBox1.Size = new Size(121, 118);
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox1.TabIndex = 9;
            pictureBox1.TabStop = false;
            // 
            // lblTimer
            // 
            lblTimer.AutoSize = true;
            lblTimer.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 0);
            lblTimer.Location = new Point(393, 9);
            lblTimer.Name = "lblTimer";
            lblTimer.Size = new Size(49, 15);
            lblTimer.TabIndex = 11;
            lblTimer.Text = "00:00:00";
            // 
            // lblProgress
            // 
            lblProgress.Location = new Point(0, 0);
            lblProgress.Name = "lblProgress";
            lblProgress.Size = new Size(100, 23);
            lblProgress.TabIndex = 0;
            // 
            // progressBarlbl
            // 
            progressBarlbl.AutoSize = true;
            progressBarlbl.BackColor = Color.Transparent;
            progressBarlbl.FlatStyle = FlatStyle.Flat;
            progressBarlbl.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point, 0);
            progressBarlbl.Location = new Point(536, 173);
            progressBarlbl.Name = "progressBarlbl";
            progressBarlbl.Size = new Size(24, 15);
            progressBarlbl.TabIndex = 12;
            progressBarlbl.Text = "0%";
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(592, 500);
            Controls.Add(progressBarlbl);
            Controls.Add(pictureBox1);
            Controls.Add(btnStop);
            Controls.Add(chkInstallAll);
            Controls.Add(lblTimer);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(btnOpenLog);
            Controls.Add(listLog);
            Controls.Add(progressBar);
            Controls.Add(btnStart);
            Controls.Add(txtFolder);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TextBox txtFolder;
        private CheckBox chkInstallAll;
        private Button btnStart;
        private ListBox listLog;
        private ProgressBar progressBar;
        private Button btnOpenLog;
        private Label label1;
        private Label label2;
        private Button btnStop;
        private PictureBox pictureBox1;
        // btnSkip removed
        private Label lblTimer;
        private Label progressBarlbl;
        private Label lblProgress;

    }
}
