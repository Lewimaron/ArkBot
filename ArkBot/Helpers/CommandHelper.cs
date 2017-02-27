﻿using Discord;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArkBot.Extensions;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using QueryMaster.GameServer;
using System.Runtime.Caching;
using ArkBot.Data;
using ArkBot.Database;
using Discord.Commands;
using System.Windows.Forms.DataVisualization.Charting;
using System.Linq.Expressions;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace ArkBot.Helpers
{
    public static class CommandHelper
    {
        public static async Task SendAnnotatedMap(Channel channel, PointF[] points, string tempFileOutputDirPath)
        {
            //send map with locations marked
            var templatePath = @"Resources\theisland-template.png";
            if (!File.Exists(templatePath)) return;

            using (var image = Image.FromFile(templatePath))
            {
                using (var g = Graphics.FromImage(image))
                {
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;

                    foreach (var loc in points)
                    {
                        //var rc = new Rectangle(81, 86, 568, 552);
                        //var rcf = new RectangleF(12.1f, 7.2f, (float)(92.1 - 12.1), (float)(87.2 - 7.2)); //87.9
                        //var x = (float)(((loc.X - rcf.Left) / rcf.Width) * rc.Width + rc.Left);
                        //var y = (float)(((loc.Y - rcf.Top) / rcf.Height) * rc.Height + rc.Top);

                        var rc = new Rectangle(81, 86, 568, 552);
                        var gx = rc.Width / 8f;
                        var gy = rc.Height / 8f;
                        var x = (float)(((loc.X - 10) / 10) * gx + rc.Left);
                        var y = (float)(((loc.Y - 10) / 10) * gy + rc.Top);

                        g.FillCircle(Brushes.Magenta, x, y, 5f);
                    }

                    var je = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Jpeg.Guid);
                    if (je == null) return;

                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);

                    var path = Path.Combine(tempFileOutputDirPath, $"{Guid.NewGuid()}.jpg");
                    image.Save(path, je, p);
                    await channel.SendFile(path);
                    File.Delete(path);
                }
            }
        }

        public static bool AreaPlotSaveAs(DataPoint[] series, string filepath)
        {
            try
            {
                using (var ch = new Chart { Width = 400, Height = 200 })
                {
                    var area = new ChartArea();
                    //area.Area3DStyle.Enable3D = true;
                    //area.Area3DStyle.LightStyle = LightStyle.Realistic;
                    area.AxisY.LabelStyle.Format = "{P0}";
                    area.AxisX.LabelStyle.Enabled = false;
                    ch.ChartAreas.Add(area);

                    var s = new Series
                    {
                        ChartType = SeriesChartType.SplineArea
                    };

                    foreach (var point in series) s.Points.Add(point);

                    ch.Series.Add(s);
                    ch.SaveImage(filepath, ChartImageFormat.Jpeg);

                    return true;
                }
            }
            catch { /* ignore exceptions */  }

            return false;
        }

        public static async Task<Tuple<ServerInfo, QueryMaster.QueryMasterCollection<Rule>, QueryMaster.QueryMasterCollection<PlayerInfo>>> GetServerStatus()
        {
            var cache = MemoryCache.Default;

            var status = cache[nameof(GetServerStatus)] as Tuple<ServerInfo, QueryMaster.QueryMasterCollection<Rule>, QueryMaster.QueryMasterCollection<PlayerInfo>>;

            if (status == null)
            {
                await Task.Factory.StartNew(() =>
                {
                    //todo: remove hardcoded value
                    using (var server = ServerQuery.GetServerInstance(QueryMaster.EngineType.Source, "85.227.28.132", 27003, throwExceptions: false, retries: 1, sendTimeout: 4000, receiveTimeout: 4000))
                    {
                        var serverInfo = server.GetInfo();
                        var serverRules = server.GetRules();
                        var playerInfo = server.GetPlayers();

                        status = new Tuple<ServerInfo, QueryMaster.QueryMasterCollection<Rule>, QueryMaster.QueryMasterCollection<PlayerInfo>>(serverInfo, serverRules, playerInfo);
                        cache.Set(nameof(GetServerStatus), status, new CacheItemPolicy { AbsoluteExpiration = DateTime.Now.AddMinutes(1) });

                        ////send rcon commands
                        //string rconPassword = "";
                        //if (server.GetControl(rconPassword))
                        //{
                        //    var result = server.Rcon.SendCommand("status");
                        //    server.Rcon.Dispose();
                        //}

                        ////listen to logs
                        //using (Logs logs = server.GetLogs(port))
                        //{
                        //    logs.Listen(x => Debug.WriteLine(x));
                        //    logs.Start();
                        //    //wait here
                        //    logs.Stop();
                        //}
                    }
                });
            }

            return status;
        }

        //todo: this method does not really belong here and should be moved elsewhere
        public static async Task<Player> GetCurrentPlayerOrSendErrorMessage(CommandEventArgs e, DatabaseContextFactory<IEfDatabaseContext> databaseContextFactory, IArkContext _context)
        {
            using (var db = databaseContextFactory.Create())
            {
                var user = db.Users.FirstOrDefault(x => x.DiscordId == (long)e.User.Id);
                if (user == null)
                {
                    await e.Channel.SendMessage($"<@{e.User.Id}>, this command can only be used after you link your Discord user with your Steam account using **!linksteam**.");
                    return null;
                }

                var player = _context.Players.FirstOrDefault(x => x.SteamId != null && x.SteamId.Equals(user.SteamId.ToString()));
                if (player == null)
                {
                    await e.Channel.SendMessage($"<@{e.User.Id}>, we have no record of you playing in the last month.");
                    return null;
                }

                return player;
            }
        }

        public static async Task SendPartitioned(Channel channel, string message)
        {
            const int maxChars = 2000;
            var _rMarkdownTokenBegin = new Regex(@"```(?<key>[^\s]*)\s+", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var markdownTokenAddedToPrev = (string)null;
            foreach (var msg in message.Partition(maxChars - 100))
            {
                var value = msg.Trim('\r', '\n');
                if (markdownTokenAddedToPrev != null)
                {
                    value = $"```{markdownTokenAddedToPrev}" + Environment.NewLine + value;
                    markdownTokenAddedToPrev = null;
                }
                var indices = value.IndexOfAll("```");
                if (indices.Length % 2 == 1)
                {
                    var m = _rMarkdownTokenBegin.Match(value, indices.Last(), value.Length - indices.Last());
                    markdownTokenAddedToPrev = m.Success ? m.Groups["key"].Value : "";
                    value = value + Environment.NewLine + "```";
                }
                await channel.SendMessage(value);
            }
        }

        public static T ParseArgs<T>(string[] args, Func<string, string> getNamedArg, T anonymousType, Action<ParseArgsConfigurationBuilder<T>> configAction = null)
            where T : class
        {
            var builder = new ParseArgsConfigurationBuilder<T>();
            configAction?.Invoke(builder);
            var config = (ParseArgsConfigurationBuilder<T>.ParseArgsConfiguration)builder;

            var list = args.ToList();
            var props = TypeDescriptor.GetProperties(typeof(T));
            var keys = props.OfType<PropertyDescriptor>().Where(x => config.Get(x.Name)?.NoPrefix != true).Select(x => x.Name).ToList();
            var values = new List<object>();
            var nextNoPrefix = -1;
            for (int i = 0; i < props.Count; i++)
            {
                var prop = props[i];
                var pc = config.Get(props[i].Name);

                if (pc?.NoPrefix == true && nextNoPrefix == int.MaxValue) return null;

                string value;

                if (pc?.Named != null)
                {
                    value = getNamedArg(pc.Named);
                }
                else
                {
                    var index = pc?.NoPrefix == true ?
                        list.Select((x, j) => new { x = x, j = j }).Skip(nextNoPrefix + 1).FirstOrDefault(x => keys.Contains(x.x, StringComparer.OrdinalIgnoreCase))?.j == nextNoPrefix + 1 ? null : (int?)nextNoPrefix
                        : list.Select((x, j) => new { x = x, j = j }).FirstOrDefault(x => x.x.Equals(prop.Name, StringComparison.OrdinalIgnoreCase))?.j;
                    if (pc?.IsRequired == true && !index.HasValue) return null;
                    else if (!index.HasValue || (pc?.Flag != true && index.Value + 1 >= list.Count))
                    {
                        values.Add(pc?.DefaultValue ?? (prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null));
                        continue;
                    }

                    if (pc?.Flag != true && pc?.UntilNextToken == true)
                    {
                        var indexUntil = list.Select((x, j) => new { x = x, j = j }).Skip(index.Value + 2).FirstOrDefault(x => keys.Contains(x.x, StringComparer.OrdinalIgnoreCase))?.j ?? list.Count;
                        value = string.Join(" ", list.Skip(index.Value + 1).Take(indexUntil - index.Value - 1));

                        nextNoPrefix = int.MaxValue;
                    }
                    else
                    {
                        value = pc?.Flag == true ? true.ToString() : list[index.Value + 1];
                        nextNoPrefix = index.Value + 1;
                    }
                }

                object converted;
                try
                {
                    converted = pc.FormatProvider == null ? Convert.ChangeType(value, prop.PropertyType) : Convert.ChangeType(value, prop.PropertyType, pc.FormatProvider);
                }
                catch
                {
                    converted = prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null;
                }

                values.Add(converted);
            }

            var result = (T)Activator.CreateInstance(typeof(T), values.ToArray());
            return result;
        }

        public static T ParseArgs<T>(CommandEventArgs e, T anonymousType, Action<ParseArgsConfigurationBuilder<T>> configAction = null)
            where T: class
        {
            return ParseArgs(e.Args, e.GetArg, anonymousType, configAction);
        }
    }

    public class ParseArgsConfigurationBuilder<T>
    {
        private ParseArgsConfiguration _configuration;

        public ParseArgsConfigurationBuilder()
        {
            _configuration = new ParseArgsConfiguration();
        }

        /// <param name="selector">Property to configure</param>
        /// <param name="defaultValue">Fallback value (cannot be used in conjunction with <paramref name="isRequired"/>)</param>
        /// <param name="named">A named argument from Discord Command (by default these are required and can only appear before any non-named arguments)</param>
        /// <param name="untilNextToken">Allow multiple arguments to be joined as one value (ex. unquoted string with spaces) terminated at the next token or end of the argument list</param>
        /// <param name="noPrefix">Primary argument which is not prefixed by a name (only one and before any prefixed arguments [for now])</param>
        /// <param name="isRequired">Property must be found in arguments for the method not to return null</param>
        /// <param name="flag">Property is a flag and lacks suffix value</param>
        /// <param name="formatProvider">Use a format provider when parsing a property value from a litteral string</param>
        /// <returns></returns>
        public ParseArgsConfigurationBuilder<T> For<TPropertyType>(Expression<Func<T, TPropertyType>> selector, TPropertyType defaultValue = default(TPropertyType), string named = null, bool untilNextToken = false, bool noPrefix = false, bool isRequired = false, bool flag = false, IFormatProvider formatProvider = null)
        {
            var name = selector.Body is MemberExpression ? (selector.Body as MemberExpression)?.Member?.Name :
                selector.Body is UnaryExpression ? ((selector.Body as UnaryExpression)?.Operand as MemberExpression)?.Member?.Name : null;
            if (name != null) _configuration.Add(name, defaultValue, named, untilNextToken, noPrefix, isRequired, flag, formatProvider);

            return this;
        }

        static public explicit operator ParseArgsConfiguration(ParseArgsConfigurationBuilder<T> self)
        {
            return self._configuration;
        }

        public class ParseArgsConfiguration
        {
            public Dictionary<string, ParseArgsConfigurationProperty> Properties { get; set; }

            public ParseArgsConfiguration()
            {
                Properties = new Dictionary<string, ParseArgsConfigurationProperty>();
            }

            public void Add(string name, object defaultValue, string named, bool untilNextToken, bool noPrefix, bool isRequired, bool flag, IFormatProvider formatProvider)
            {
                Properties.Add(name, new ParseArgsConfigurationProperty { Named = named, DefaultValue = defaultValue, UntilNextToken = untilNextToken, NoPrefix = noPrefix, IsRequired = isRequired, Flag = flag, FormatProvider = formatProvider });
            }

            public ParseArgsConfigurationProperty Get(string name)
            {
                return Properties.ContainsKey(name) ? Properties[name] : null;
            }
        }

        public class ParseArgsConfigurationProperty
        {
            public string Named { get; set; }
            public bool UntilNextToken { get; set; }
            public IFormatProvider FormatProvider { get; set; }
            public bool NoPrefix { get; set; }
            public bool IsRequired { get; set; }
            public bool Flag { get; set; }

            public object DefaultValue { get; set; }
        }
    }
}