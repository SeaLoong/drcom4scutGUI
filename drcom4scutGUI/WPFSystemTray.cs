using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace drcom4scutGUI
{
    public class WPFSystemTray
    {
        /// <summary>
        /// 设置系统托盘
        /// </summary>
        /// <param name="pars">最小化参数</param>
        /// <param name="menuList"></param>
        /// <returns></returns>
        public static NotifyIcon SetSystemTray(SystemTrayParameter pars, List<SystemTrayMenu> menuList)
        {
            NotifyIcon notifyIcon = new NotifyIcon
            {
                Visible = true
            };
            if (pars.Icon != null)
            {
                notifyIcon.Icon = pars.Icon;//程序图标
            }
            if (!string.IsNullOrWhiteSpace(pars.MinText))
            {
                notifyIcon.Text = pars.MinText;//最小化到托盘时，鼠标悬浮时显示的文字
            }
            if (!string.IsNullOrWhiteSpace(pars.TipText))
            {
                notifyIcon.BalloonTipText = pars.TipText; //设置系统托盘启动时显示的文本
                notifyIcon.ShowBalloonTip(pars.Time == 0 ? 100 : pars.Time);//显示时长
            }
            if (pars.Click != null)
            {
                notifyIcon.MouseClick += pars.Click;
            }
            if (pars.DbClick != null)
            {
                notifyIcon.MouseDoubleClick += pars.DbClick;
            }
            notifyIcon.ContextMenuStrip = GetMenuStrip(menuList);
            return notifyIcon;
        }
        /// <summary>
        /// 设置系统托盘的菜单属性
        /// </summary>
        /// <param name="menus"></param>
        /// <returns></returns>
        static ContextMenuStrip GetMenuStrip(List<SystemTrayMenu> menus)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem[] menuArray = new ToolStripMenuItem[menus.Count];
            int i = 0;
            foreach (SystemTrayMenu item in menus)
            {
                ToolStripMenuItem menuItem = new ToolStripMenuItem
                {
                    Text = item.Text
                };
                menuItem.Click += item.Click;
                menuArray[i++] = menuItem;
            }
            menu.Items.AddRange(menuArray);
            return menu;
        }
    }

    /// <summary>
    /// 系统托盘参数
    /// </summary>
    public class SystemTrayParameter
    {
        public SystemTrayParameter(Icon Icon, string MinText, string TipText, int Time)
        {
            this.Icon = Icon;
            this.MinText = MinText;
            this.TipText = TipText;
            this.Time = Time;
        }
        /// <summary>
        /// 托盘显示图标
        /// </summary>
        public Icon Icon { get; set; }
        /// <summary>
        /// 最小化悬浮时文本
        /// </summary>
        public string MinText { get; set; }
        /// <summary>
        /// 最小化启动时文本
        /// </summary>
        public string TipText { get; set; }
        /// <summary>
        /// 最小化启动时文本显示时长
        /// </summary>
        public int Time { get; set; }
        /// <summary>
        /// 最小化单击事件
        /// </summary>
        public MouseEventHandler Click { get; set; }
        /// <summary>
        /// 最小化双击事件
        /// </summary>
        public MouseEventHandler DbClick { get; set; }
    }
    /// <summary>
    /// 右键菜单
    /// </summary>
    public class SystemTrayMenu
    {
        public SystemTrayMenu(string Text, EventHandler Click)
        {
            this.Text = Text;
            this.Click = Click;
        }
        /// <summary>
        /// 菜单文本
        /// </summary>
        public string Text { get; set; }
        /// <summary>
        /// 菜单单击事件
        /// </summary>
        public EventHandler Click { get; set; }
    }
}
