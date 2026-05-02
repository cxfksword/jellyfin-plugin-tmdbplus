using System.Net;
using System.Reflection;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TMDbPlus.Configuration;


/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public const int MAX_CAST_MEMBERS = 15;
    public const int MAX_SEARCH_RESULT = 5;

    /// <summary>
    /// 插件版本
    /// </summary>
    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    /// <summary>
    /// 启用tmdb获取成人内容
    /// </summary>
    public bool EnableTmdbAdult { get; set; } = false;
    /// <summary>
    /// 是否获取tmdb分级信息
    /// </summary>
    public bool EnableTmdbOfficialRating { get; set; } = true;
    /// <summary>
    /// tmdb api key
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;
    /// <summary>
    /// tmdb api host
    /// </summary>
    public string TmdbHost { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器类型，0-禁用，1-http，2-https，3-socket5
    /// </summary>
    public string TmdbProxyType { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器host
    /// </summary>
    public string TmdbProxyPort { get; set; } = string.Empty;
    /// <summary>
    /// 代理服务器端口
    /// </summary>
    public string TmdbProxyHost { get; set; } = string.Empty;


    /// <summary>
    /// 启用AI翻译角色名
    /// </summary>
    public bool EnableAiTranslateCharacter { get; set; } = false;
    /// <summary>
    /// AI接口BaseUrl（OpenAI兼容）
    /// </summary>
    public string AiBaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// AI接口ApiKey
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;
    /// <summary>
    /// AI模型名称
    /// </summary>
    public string AiModel { get; set; } = string.Empty;
    /// <summary>
    /// 自定义翻译Prompt（留空使用默认）
    /// </summary>
    public string AiTranslatePrompt { get; set; } = string.Empty;

    public const string DEFAULT_AI_TRANSLATE_PROMPT = "下面是影片 {{title}}({{year}}) 每个角色的英文名，每行一个，帮我翻译为对应角色的中文名，输出格式：英文角色名:中文角色名，输出格式也保持每行一个角色名，不需要添加其他说明文字，假如没找到对应的中文角色名，该角色就不需要输出，不要自行直接翻译";

    public IWebProxy GetTmdbWebProxy()
    {

        if (!string.IsNullOrEmpty(TmdbProxyType))
        {
            return new WebProxy($"{TmdbProxyType}://{TmdbProxyHost}:{TmdbProxyPort}", true);
        }

        return null;
    }
}
