using System;
using System.Reflection;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("TestPlugin", "AI", "1.0")]
    public class TestPlugin : RustPlugin
    {
        private void Init()
        {
            try
            {
                var type = typeof(MapImageRenderer);
                if (type == null)
                {
                    Puts("MapImageRenderer not found.");
                    return;
                }
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    Puts($"Method: {method.Name}, Return: {method.ReturnType}, Params: {method.GetParameters().Length}");
                    foreach (var param in method.GetParameters())
                    {
                        Puts($"  Param: {param.Name} ({param.ParameterType})");
                    }
                }
            }
            catch (Exception ex)
            {
                Puts(ex.ToString());
            }
        }
    }
}
