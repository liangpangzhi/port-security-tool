using System;
using System.Threading;

namespace PortSecurityTool
{
    /// <summary>
    /// 单实例控制：以命名互斥体占位，进程退出时由系统自动释放。
    /// 用 Mutex 而不是「查窗口标题」，是为了消除两个进程同时启动时的竞态
    /// （都还没建窗口、互查不到对方）。
    /// </summary>
    internal static class SingleInstance
    {
        // Local\ 作用域＝当前登录会话：同一用户重复启动被拦截，多用户/远程会话互不影响
        private const string MutexName = @"Local\PstPortTool.SingleInstance";

        private static Mutex _mutex;

        /// <summary>
        /// 尝试成为唯一实例。true = 本进程拿到所有权（互斥体在进程退出前一直持有）；
        /// false = 已有实例在运行，调用方应唤起旧窗口后退出。
        /// </summary>
        public static bool TryAcquire()
        {
            try
            {
                bool created;
                _mutex = new Mutex(true, MutexName, out created);
                if (created) return true;
                // 名字已存在：说明有实例持有着（可能是别的进程，也可能是本进程重复调用）
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
                return false;
            }
            catch
            {
                // 互斥体创建异常（权限/句柄问题）时不要傻等，按单实例放行，避免程序打不开
                return true;
            }
        }
    }
}
