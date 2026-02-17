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
            btnStart = new Button();
            listLog = new ListBox();
            progressBar = new ProgressBar();
            btnOpenLog = new Button();
            chkForceAgent = new CheckBox();
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
            txtFolder.Location = new Point(12, 40);
            txtFolder.Name = "txtFolder";
            txtFolder.Size = new Size(430, 23);
            txtFolder.TabIndex = 5;
            // 
            // btnStart
            // 
            btnStart.Location = new Point(12, 73);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(430, 30);
            btnStart.TabIndex = 3;
            btnStart.Text = "Start Installation";
            btnStart.UseVisualStyleBackColor = true;
            btnStart.Click += btnStart_Click;
            // 
            // listLog
            // 
            listLog.Location = new Point(12, 148);
            listLog.Name = "listLog";
            listLog.Size = new Size(568, 229);
            listLog.TabIndex = 1;
            // 
            // progressBar
            // 
            progressBar.Location = new Point(12, 113);
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(568, 25);
            progressBar.TabIndex = 2;
            // 
            // btnOpenLog
            // 
            btnOpenLog.Location = new Point(12, 388);
            btnOpenLog.Name = "btnOpenLog";
            btnOpenLog.Size = new Size(172, 30);
            btnOpenLog.TabIndex = 0;
            btnOpenLog.Text = "Open log file";
            btnOpenLog.UseVisualStyleBackColor = true;
            btnOpenLog.Click += btnOpenLog_Click;
            // 
            // chkForceAgent
            // 
            chkForceAgent.Location = new Point(200, 388);
            chkForceAgent.Name = "chkForceAgent";
            chkForceAgent.Size = new Size(220, 30);
            chkForceAgent.TabIndex = 13;
            chkForceAgent.Text = "Always use elevated agent";
            chkForceAgent.UseVisualStyleBackColor = true;
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
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 8.25F, FontStyle.Italic, GraphicsUnit.Point, 0);
            label2.Location = new Point(509, 425);
            label2.Name = "label2";
            label2.Size = new Size(70, 13);
            label2.TabIndex = 7;
            label2.Text = "LunasSystems";
            // 
            // btnStop
            // 
            btnStop.Location = new Point(12, 73);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(430, 30);
            btnStop.TabIndex = 8;
            btnStop.Text = "Stop Installation";
            btnStop.UseVisualStyleBackColor = true;
            btnStop.Click += btnStop_Click;
            // 
            // pictureBox1
            // 
            pictureBox1.Image = Properties.Resources.AIA;
            pictureBox1.Location = new Point(460, 9);
            pictureBox1.Name = "pictureBox1";
            pictureBox1.Size = new Size(120, 98);
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
            progressBarlbl.Location = new Point(254, 118);
            progressBarlbl.Name = "progressBarlbl";
            progressBarlbl.Size = new Size(24, 15);
            progressBarlbl.TabIndex = 12;
            progressBarlbl.Text = "0%";
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(592, 449);
            Controls.Add(progressBarlbl);
            Controls.Add(pictureBox1);
            Controls.Add(btnStop);
            Controls.Add(lblTimer);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(btnOpenLog);
            Controls.Add(listLog);
            Controls.Add(chkForceAgent);
            Controls.Add(progressBar);
            Controls.Add(btnStart);
            Controls.Add(txtFolder);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            Text = "Auto Installer";
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
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
        private Label label2;
        private Button btnStop;
        private PictureBox pictureBox1;
        // btnSkip removed
        private Label lblTimer;
        private Label progressBarlbl;
        private Label lblProgress;
        private CheckBox chkForceAgent;
    }
}
