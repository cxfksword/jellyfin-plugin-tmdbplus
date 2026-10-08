using Jellyfin.Plugin.TMDbPlus.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace Jellyfin.Plugin.TMDbPlus.Test
{
    [TestClass]
    public class TmdbIdAttributeTest
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "hh:mm:ss ";
                }));

        [TestMethod]
        public void RemoveSeasonSuffix_ShouldKeepTrailingNumberAsTitle()
        {
            var provider = new TestableBaseProvider(loggerFactory);

            // 标题自带的尾数（第X季/Season X 除外）不应被删除
            Assert.AreEqual("命运石之门 0", provider.ExposedRemoveSeasonSuffix("命运石之门 0"));
            Assert.AreEqual("命运石之门0", provider.ExposedRemoveSeasonSuffix("命运石之门0"));
            Assert.AreEqual("ID-0", provider.ExposedRemoveSeasonSuffix("ID-0"));
            Assert.AreEqual("神探狄仁杰2", provider.ExposedRemoveSeasonSuffix("神探狄仁杰2"));
            Assert.AreEqual("白色相簿2", provider.ExposedRemoveSeasonSuffix("白色相簿2"));

            // 真正的季后缀仍需去除
            Assert.AreEqual("向往的生活", provider.ExposedRemoveSeasonSuffix("向往的生活 第2季"));
            Assert.AreEqual("Bright Future", provider.ExposedRemoveSeasonSuffix("Bright Future Season 2"));
        }

        [TestMethod]
        public void GuessSeasonNumberByDirectoryName_ShouldSupportSeasonFormats()
        {
            var provider = new TestableBaseProvider(loggerFactory);

            // Season XX 系列命名应由 SeasonPathParser 兜底识别
            Assert.AreEqual(2, provider.ExposedGuessSeasonNumber("/media/神探狄仁杰/Season 2"));
            Assert.AreEqual(1, provider.ExposedGuessSeasonNumber("/media/Bright Future/S01"));
            Assert.AreEqual(2, provider.ExposedGuessSeasonNumber("/media/向往的生活/第2季"));
        }

        private sealed class TestableBaseProvider : BaseProvider
        {
            public TestableBaseProvider(ILoggerFactory loggerFactory)
                : base(new DefaultHttpClientFactory(), loggerFactory.CreateLogger<TestableBaseProvider>(), null!, null!, null!)
            {
            }

            public string ExposedRemoveSeasonSuffix(string name) => RemoveSeasonSuffix(name);

            public int? ExposedGuessSeasonNumber(string path) => GuessSeasonNumberByDirectoryName(path);
        }
    }
}
