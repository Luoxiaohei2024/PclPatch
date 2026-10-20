using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using HarmonyLib;

namespace PclPatch
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 以启动器自身所在目录为工作目录，保证 PCL 能找到其依赖与配置。
            string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string pclPath = Path.Combine(launcherDir, "Plain Craft Launcher 2.exe");
                if (!File.Exists(pclPath))
                {
                    MessageBox.Show("将此启动器放到 Plain Craft Launcher 2.exe 同一目录后再运行。", "PclPatch");
                    return;
                }

                Environment.CurrentDirectory = launcherDir;

                // 以 加载程序集 的方式载入 PCL，
                Assembly pcl = Assembly.LoadFrom(pclPath);
                Type applicationType = pcl.GetType("PCL.Application", throwOnError: true);

                object application = Activator.CreateInstance(applicationType, nonPublic: true);

                // Harmony 
                var harmony = new Harmony("local.pcl.minecraft-download-page");
                PatchPageDownload(pcl, harmony);

                MethodInfo initializeComponent = applicationType.GetMethod("InitializeComponent",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                initializeComponent.Invoke(application, null);

                // 启动 WPF 消息循环，接管整个应用生命周期。
                ((Application)application).Run();
            }
            catch (Exception ex)
            {
                // 启动阶段异常无法通过 UI 正常反馈，写日志并弹窗提示原始异常。
                string log = Path.Combine(launcherDir, "PclPatch.log");
                File.WriteAllText(log, ex.ToString());
                MessageBox.Show(ex.GetBaseException().ToString(), "PclPatch", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 定位需要打补丁的两个页面类型
        private static void PatchPageDownload(Assembly pcl, Harmony harmony)
        {
            Type installPage = pcl.GetType("PCL.PageDownloadInstall", throwOnError: true);
            Type modifyPage = pcl.GetType("PCL.PageDownloadModify", throwOnError: true);

            MethodInfo loaderInit = installPage.GetMethod("LoaderInit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (loaderInit == null) throw new MissingMethodException(installPage.FullName, "LoaderInit");

            harmony.Patch(loaderInit, prefix: new HarmonyMethod(typeof(Program), nameof(LoaderInitPrefix)));

            MethodInfo loadInstanceFinished = modifyPage.GetMethod("LoadInstanceFinished", BindingFlags.Instance | BindingFlags.NonPublic);
            if (loadInstanceFinished != null)
                harmony.Patch(loadInstanceFinished, postfix: new HarmonyMethod(typeof(Program), nameof(LoadInstanceFinishedPostfix)));
        }

        // 替换 PageDownloadInstall.LoaderInit 的原始逻辑：
        private static bool LoaderInitPrefix(object __instance)
        {
            try
            {
                Type pageType = __instance.GetType();

                // 执行页面原有的控件初始化
                pageType.GetMethod("ControlInit", BindingFlags.Instance | BindingFlags.Public).Invoke(__instance, null);

                Assembly pcl = pageType.Assembly;

                Type modDownload = pcl.GetType("PCL.ModDownload", throwOnError: true);
                object clientListLoader = modDownload.GetField("DlClientListLoader", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                Type loaderType = clientListLoader.GetType();
                Type[] genericArgs = loaderType.GetGenericArguments();

                MethodInfo finishMethod = pageType.GetMethod("LoadMinecraft_OnFinish", BindingFlags.Instance | BindingFlags.NonPublic);
                Type finishDelegateType = typeof(Action<>).MakeGenericType(loaderType);
                var finishThunk = new DynamicMethod("PclPatch_Finish", typeof(void),
                    new[] { typeof(object), loaderType }, typeof(Program).Module, skipVisibility: true);
                ILGenerator finishIl = finishThunk.GetILGenerator();
                finishIl.Emit(OpCodes.Ldarg_0); 
                finishIl.Emit(OpCodes.Castclass, pageType);
                finishIl.Emit(OpCodes.Callvirt, finishMethod); 
                finishIl.Emit(OpCodes.Ret);
                Delegate finish = finishThunk.CreateDelegate(finishDelegateType, __instance);


                MethodInfo pageLoaderInit = pageType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(m => m.Name == "PageLoaderInit" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)
                    .Single(m => m.GetParameters().Length == 8);
                pageLoaderInit = pageLoaderInit.MakeGenericMethod(genericArgs);

                object GetPageProperty(string name) => pageType.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(__instance, null);

                pageLoaderInit.Invoke(__instance, new[] {
                    GetPageProperty("LoadMinecraft"), GetPageProperty("PanLoad"), GetPageProperty("PanAllBack"),
                    null, clientListLoader, finish, null, true
                });
                return false; 
            }
            catch (Exception ex)
            {
                // 补丁失败时回到原生逻辑，避免页面完全不可用。
                MessageBox.Show("无法接回当前 PCL 构建的原生下载页：\n" + ex.GetBaseException().Message,
                    "PCL Patch", MessageBoxButton.OK, MessageBoxImage.Error);
                return true;
            }
        }


        //版本设置->修改->Minecraft 的选项显示
        private static void LoadInstanceFinishedPostfix(object __instance)
        {
            try
            {
                Type pageType = __instance.GetType();
                object card = pageType.GetProperty("CardMinecraft",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(__instance, null);
                card.GetType().GetProperty("Visibility").SetValue(card, Visibility.Visible, null);
            }
            catch { } 
        }
    }
}
