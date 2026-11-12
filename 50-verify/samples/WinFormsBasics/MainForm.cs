using System.Drawing;
using System.Windows.Forms;

namespace LinuxDesktopPerfect.Samples.WinFormsBasics
{
    public class MainForm : Form
    {
        public MainForm(string scr)
        {
            Text = "WinFormsBasics" + (scr == null ? "" : " [" + scr + "]");
            AccessibleName = "WinFormsBasics";
            ClientSize = new Size(900, 700);

            Button button = new Button();
            button.Text = "确定";
            button.AccessibleName = "确定";
            button.Location = new Point(12, 12);
            Controls.Add(button);

            TextBox textBox = new TextBox();
            textBox.AccessibleName = "输入";
            textBox.Location = new Point(12, 48);
            Controls.Add(textBox);

            ListBox listBox = new ListBox();
            listBox.AccessibleName = "列表";
            listBox.Location = new Point(12, 80);
            listBox.Items.Add("项 1");
            Controls.Add(listBox);

            ComboBox comboBox = new ComboBox();
            comboBox.AccessibleName = "下拉";
            comboBox.Location = new Point(12, 160);
            comboBox.Items.Add("选项一");
            Controls.Add(comboBox);

            Panel panel = new Panel();
            panel.AccessibleName = "子窗体容器";
            panel.Location = new Point(12, 196);
            panel.Size = new Size(200, 60);
            panel.BackColor = Color.WhiteSmoke;
            Controls.Add(panel);

            MenuStrip menu = new MenuStrip();
            menu.AccessibleName = "菜单";
            ToolStripMenuItem fileItem = new ToolStripMenuItem("文件");
            fileItem.AccessibleName = "文件";
            menu.Items.Add(fileItem);
            Controls.Add(menu);

            // 缺失控件占位：缺陷编号未分配，先用 D-xxx 占位（不得借用 D-001）
            Label missing = new Label();
            missing.Text = "缺失控件占位";
            missing.AccessibleName = "缺失控件 SamplePlaceholder D-xxx";
            missing.Location = new Point(12, 268);
            missing.AutoSize = true;
            Controls.Add(missing);

            Label msaa = new Label();
            msaa.Text = "msaa=off";
            msaa.AccessibleName = "msaa=off";
            msaa.Location = new Point(12, 300);
            msaa.AutoSize = true;
            Controls.Add(msaa);
        }
    }
}
