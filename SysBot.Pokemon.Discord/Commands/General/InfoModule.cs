using Discord;
using Discord.Commands;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    // src: https://github.com/foxbot/patek/blob/master/src/Patek/Modules/InfoModule.cs
    // ISC License (ISC)
    // Copyright 2017, Christopher F. <foxbot@protonmail.com>
    public class InfoModule : ModuleBase<SocketCommandContext>
    {
        private const string detail = "Pokémon Legends";
        private const string link = "http://pokelegend.co/";
        private const string vers = "1.3.0";
        private const string Game = "Brilliant Diamond and Shinning Pearl";

        [Command("info")]
        [Alias("about", "whoami", "owner")]
        public async Task InfoAsync()
        {
            var app = await Context.Client.GetApplicationInfoAsync().ConfigureAwait(false);

            var builder = new EmbedBuilder
            {
                Color = new Color(114, 137, 218),
                Description = detail,
            };

            builder.AddField("PokeLegend",
                $"- [Pokémon Legends]({link})\n" +
                $"- {Game}\n" +
                $"- {Format.Bold("Owner")}: {app.Owner}\n" +
                $"- {Format.Bold("Uptime")}: {GetUptime()}\n" +
                $"- {Format.Bold("Version")}: {vers}\n"
                );

            await ReplyAsync("**PokeLegend Information**", embed: builder.Build()).ConfigureAwait(false);
        }




        private static string GetUptime() => (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"dd\.hh\:mm\:ss");
        private static string GetHeapSize() => Math.Round(GC.GetTotalMemory(true) / (1024.0 * 1024.0), 2).ToString(CultureInfo.CurrentCulture);

        private static string GetBuildTime()
        {
            var assembly = Assembly.GetEntryAssembly();
            return File.GetLastWriteTime(assembly.Location).ToString(@"yy-MM-dd\.hh\:mm");
        }

        public static string GetCoreDate() => GetDateOfDll("PKHeX.Core.dll");
        public static string GetALMDate() => GetDateOfDll("PKHeX.Core.AutoMod.dll");

        private static string GetDateOfDll(string dll)
        {
            var folder = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var path = Path.Combine(folder, dll);
            var date = File.GetLastWriteTime(path);
            return date.ToString(@"yy-MM-dd\.hh\:mm");
        }
    }
}