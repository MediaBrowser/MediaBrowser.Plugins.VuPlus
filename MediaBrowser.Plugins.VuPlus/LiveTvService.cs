using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.VuPlus.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Xml;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Plugins.VuPlus
{
    /// <summary>
    /// Class LiveTvService
    /// </summary>
    public class LiveTvService : BaseTunerHost, ITunerHost
    {
        private readonly IHttpClient _httpClient;

        public LiveTvService(IHttpClient httpClient, IServerApplicationHost appHost)
            : base(appHost)
        {
            _httpClient = httpClient;
        }

        public override string Name => Plugin.StaticName;

        public override string Type => "vuplus";

        public override string SetupUrl
        {
            get { return Plugin.GetPluginPageUrl("vuplus"); }
        }

        public override TunerHostInfo GetDefaultConfiguration()
        {
            var tuner = base.GetDefaultConfiguration();

            tuner.Url = "http://localhost:8000";

            SetCustomOptions(tuner, new VuPlusTunerOptions());

            return tuner;
        }

        /// <summary>
        /// Ensure that we are connected to the VuPlus server
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<string> EnsureConnectionAsync(TunerHostInfo tuner, VuPlusTunerOptions config, CancellationToken cancellationToken)
        {
            Logger.Info("[VuPlus] Start EnsureConnectionAsync");

            // log settings
            Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync Url: {0}", tuner.Url));
            Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync StreamingPort: {0}", config.StreamingPort));
            if (string.IsNullOrEmpty(config.WebInterfaceUsername))
                Logger.Info("[VuPlus] EnsureConnectionAsync WebInterfaceUsername: ");
            else
                Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync WebInterfaceUsername: {0}", "********"));
            if (string.IsNullOrEmpty(config.WebInterfacePassword))
                Logger.Info("[VuPlus] EnsureConnectionAsync WebInterfacePassword: ");
            else
                Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync WebInterfaceUsername: {0}", "********"));
            Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync OnlyOneBouquet: {0}", config.OnlyOneBouquet));
            Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync TVBouquet: {0}", config.TVBouquet));
            Logger.Info(string.Format("[VuPlus] EnsureConnectionAsync EnableDebugLogging: {0}", config.EnableDebugLogging));

            if (config.OnlyOneBouquet)
            {
                if (string.IsNullOrEmpty(config.TVBouquet))
                {
                    Logger.Error("[VuPlus] TV Bouquet must be configured if Fetch only one TV bouquet selected.");
                    throw new InvalidOperationException("VuPlus TVBouquet must be configured if Fetch only one TV bouquet selected.");
                }
            }

            Logger.Info("[VuPlus] EnsureConnectionAsync Validation of config parameters completed");

            string tvBouquetSRef;

            if (config.OnlyOneBouquet)
            {
                // connect to VuPlus box to test connectivity and at same time get sRef for TV Bouquet.
                tvBouquetSRef = await InitiateSession(tuner, config, cancellationToken, config.TVBouquet).ConfigureAwait(false);
            }
            else
            {
                // connect to VuPlus box to test connectivity.
                String resultNotRequired = await InitiateSession(tuner, config, cancellationToken, null).ConfigureAwait(false);
                tvBouquetSRef = null;
            }

            return tvBouquetSRef;
        }


        /// <summary>
        /// Checks connection to VuPlus and retrieves service reference for channel if only one bouquet.
        /// </summary>
        /// <returns>Task{String>}.</returns>
        public async Task<String> InitiateSession(TunerHostInfo tuner, VuPlusTunerOptions config, CancellationToken cancellationToken, String tvBouquet)
        {
            Logger.Info("[VuPlus] Start InitiateSession, validates connection and returns Bouquet reference if required");
            //await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

            var baseUrl = tuner.Url.TrimEnd('/');

            var url = string.Format("{0}/web/getservices", baseUrl);
            Logger.Info("[VuPlus] InitiateSession url: {0}", url);

            var options = new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url,
            };

            if (!string.IsNullOrEmpty(config.WebInterfaceUsername))
            {
                string authInfo = config.WebInterfaceUsername + ":" + config.WebInterfacePassword;
                authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
                options.RequestHeaders["Authorization"] = "Basic " + authInfo;
            }

            using (var stream = await _httpClient.Get(options).ConfigureAwait(false))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    string xmlResponse = reader.ReadToEnd();
                    UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] InitiateSession response: {0}", xmlResponse));

                    try
                    {
                        var xml = new XmlDocument();
                        xml.LoadXml(xmlResponse);

                        String tvBouquetReference = null;

                        XmlNodeList e2services = xml.GetElementsByTagName("e2service");

                        // If TV Bouquet passed find associated service reference
                        if (!string.IsNullOrEmpty(tvBouquet))
                        {
                            foreach (XmlNode xmlNode in e2services)
                            {
                                var channelInfo = new ChannelInfo()
                                {
                                    TunerHostId = tuner.Id
                                };

                                var e2servicereference = "?";
                                var e2servicename = "?";

                                foreach (XmlNode node in xmlNode.ChildNodes)
                                {
                                    if (node.Name == "e2servicereference")
                                    {
                                        e2servicereference = node.InnerText;
                                    }
                                    else if (node.Name == "e2servicename")
                                    {
                                        e2servicename = node.InnerText;
                                    }
                                }
                                if (tvBouquet == e2servicename)
                                {
                                    tvBouquetReference = e2servicereference;
                                    return tvBouquetReference;
                                }
                            }
                            // make sure we have found the TV Bouquet
                            if (!string.IsNullOrEmpty(tvBouquet))
                            {
                                Logger.Error("[VuPlus] Failed to find TV Bouquet specified in VuPlus configuration.");
                                throw new ApplicationException("Failed to find TV Bouquet specified in VuPlus configuration.");
                            }
                        }
                        return tvBouquetReference;
                    }
                    catch (Exception e)
                    {
                        Logger.Error("[VuPlus] Failed to parse services information.");
                        Logger.Error(string.Format("[VuPlus] InitiateSession error: {0}", e.Message));
                        throw new ApplicationException("Failed to connect to VuPlus.");
                    }

                }
            }
        }

        private string AddAuthToUrl(string url, VuPlusTunerOptions config)
        {
            if (!string.IsNullOrEmpty(config.WebInterfaceUsername))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    var builder = new UriBuilder(uri);

                    builder.UserName = config.WebInterfaceUsername;
                    builder.Password = config.WebInterfacePassword;

                    return builder.Uri.ToString().TrimEnd('/');
                }
            }
            return url;
        }

        /// <summary>
        /// Gets the channels async.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IEnumerable{ChannelInfo}}.</returns>
        protected override async Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var config = GetProviderOptions<VuPlusTunerOptions>(tuner);

            Logger.Info("[VuPlus] Start GetChannelsAsync, retrieve all channels");
            var tvBouquetSRef = await EnsureConnectionAsync(tuner, config, cancellationToken).ConfigureAwait(false);

            var baseUrl = tuner.Url.TrimEnd('/');

            var baseUrlPicon = baseUrl;

            var url = "";
            if (string.IsNullOrEmpty(tvBouquetSRef))
            {
                url = string.Format("{0}/web/getservices", baseUrl);
            }
            else
            {
                url = string.Format("{0}/web/getservices?sRef={1}", baseUrl, tvBouquetSRef);
            }

            Logger.Info("[VuPlus] GetChannelsAsync url: {0}", url);

            var options = new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url,
                UserAgent = "vuplus-pvraddon-agent/1.0"
            };

            if (!string.IsNullOrEmpty(config.WebInterfaceUsername))
            {
                string authInfo = config.WebInterfaceUsername + ":" + config.WebInterfacePassword;
                authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
                options.RequestHeaders["Authorization"] = "Basic " + authInfo;

                baseUrlPicon = AddAuthToUrl(baseUrlPicon, config);
            }

            using (var stream = await _httpClient.Get(options).ConfigureAwait(false))
            {
                using (StreamReader reader = new StreamReader(stream))
                {

                    string xmlResponse = reader.ReadToEnd();
                    UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetChannelsAsync response: {0}", xmlResponse));

                    try
                    {
                        var xml = new XmlDocument();
                        xml.LoadXml(xmlResponse);

                        List<ChannelInfo> channelInfos = new List<ChannelInfo>();

                        if (string.IsNullOrEmpty(tvBouquetSRef))
                        {
                            // Load channels from all TV Bouquets
                            Logger.Info("[VuPlus] GetChannelsAsync for all TV Bouquets");

                            XmlNodeList e2services = xml.GetElementsByTagName("e2service");
                            foreach (XmlNode xmlNode in e2services)
                            {
                                var channelInfo = new ChannelInfo()
                                {
                                    TunerHostId = tuner.Id
                                };
                                var e2servicereference = "?";
                                var e2servicename = "?";

                                foreach (XmlNode node in xmlNode.ChildNodes)
                                {
                                    if (node.Name == "e2servicereference")
                                    {
                                        e2servicereference = node.InnerText;
                                    }
                                    else if (node.Name == "e2servicename")
                                    {
                                        e2servicename = node.InnerText;
                                    }
                                }

                                // get all channels for TV Bouquet
                                List<ChannelInfo> channelInfosForBouquet = await GetChannelsForTVBouquetAsync(tuner, config, cancellationToken, e2servicereference).ConfigureAwait(false);

                                // store all channels for TV Bouquet
                                channelInfos.AddRange(channelInfosForBouquet);
                            }

                            return channelInfos;
                        }
                        else
                        {
                            // Load channels for specified TV Bouquet only
                            int count = 1;

                            XmlNodeList e2services = xml.GetElementsByTagName("e2service");
                            foreach (XmlNode xmlNode in e2services)
                            {
                                var channelInfo = new ChannelInfo()
                                {
                                    TunerHostId = tuner.Id
                                };

                                var e2servicereference = "?";
                                var e2servicename = "?";

                                foreach (XmlNode node in xmlNode.ChildNodes)
                                {
                                    if (node.Name == "e2servicereference")
                                    {
                                        e2servicereference = node.InnerText;
                                    }
                                    else if (node.Name == "e2servicename")
                                    {
                                        e2servicename = node.InnerText;
                                    }
                                }

                                // Check whether the current element is not just a label
                                if (!e2servicereference.StartsWith("1:64:"))
                                {
                                    //check for radio channel
                                    if (e2servicereference.ToUpper().Contains("RADIO"))
                                        channelInfo.ChannelType = ChannelType.Radio;
                                    else
                                        channelInfo.ChannelType = ChannelType.TV;

                                    channelInfo.HasImage = true;
                                    channelInfo.Id = CreateEmbyChannelId(tuner, e2servicereference);

                                    // image name is name is e2servicereference with last char removed, then replace all : with _, then add .png
                                    var imageName = e2servicereference.Remove(e2servicereference.Length - 1);
                                    imageName = imageName.Replace(":", "_");
                                    imageName = imageName + ".png";
                                    //var imageUrl = string.Format("{0}/picon/{1}", baseUrl, imageName);
                                    var imageUrl = string.Format("{0}/picon/{1}", baseUrlPicon, imageName);

                                    //channelInfo.ImageUrl = WebUtility.UrlEncode(imageUrl);
                                    channelInfo.ImageUrl = imageUrl;

                                    channelInfo.Name = e2servicename;
                                    channelInfo.Number = count.ToString();

                                    channelInfos.Add(channelInfo);
                                    count = count + 1;
                                }
                                else
                                {
                                    Logger.Info("[VuPlus] ignoring channel label " + e2servicereference);
                                }
                            }
                        }
                        return channelInfos;
                    }
                    catch (Exception e)
                    {
                        Logger.Error("[VuPlus] Failed to parse channel information.");
                        Logger.Error(string.Format("[VuPlus] GetChannelsAsync error: {0}", e.Message));
                        throw new ApplicationException("Failed to parse channel information.");
                    }
                }
            }
        }


        /// <summary>
        /// Gets the channels async.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{List<ChannelInfo>}.</returns>
        public async Task<List<ChannelInfo>> GetChannelsForTVBouquetAsync(TunerHostInfo tuner, VuPlusTunerOptions config, CancellationToken cancellationToken, String sRef)
        {
            Logger.Info("[VuPlus] Start GetChannelsForTVBouquetAsync, retrieve all channels for TV Bouquet " + sRef);
            await EnsureConnectionAsync(tuner, config, cancellationToken).ConfigureAwait(false);

            var baseUrl = tuner.Url.TrimEnd('/');

            var baseUrlPicon = AddAuthToUrl(baseUrl, config);

            var url = string.Format("{0}/web/getservices?sRef={1}", baseUrl, sRef);

            Logger.Info("[VuPlus] GetChannelsForTVBouquetAsync url: {0}", url);

            var options = new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url,
                UserAgent = "vuplus-pvraddon-agent/1.0"
            };

            if (!string.IsNullOrEmpty(config.WebInterfaceUsername))
            {
                string authInfo = config.WebInterfaceUsername + ":" + config.WebInterfacePassword;
                authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
                options.RequestHeaders["Authorization"] = "Basic " + authInfo;
            }

            using (var stream = await _httpClient.Get(options).ConfigureAwait(false))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    string xmlResponse = reader.ReadToEnd();
                    UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetChannelsForTVBouquetAsync response: {0}", xmlResponse));

                    try
                    {
                        var xml = new XmlDocument();
                        xml.LoadXml(xmlResponse);

                        List<ChannelInfo> channelInfos = new List<ChannelInfo>();

                        // Load channels for specified TV Bouquet only

                        int count = 1;

                        XmlNodeList e2services = xml.GetElementsByTagName("e2service");
                        foreach (XmlNode xmlNode in e2services)
                        {
                            var channelInfo = new ChannelInfo()
                            {
                                TunerHostId = tuner.Id
                            };

                            var e2servicereference = "?";
                            var e2servicename = "?";

                            foreach (XmlNode node in xmlNode.ChildNodes)
                            {
                                if (node.Name == "e2servicereference")
                                {
                                    e2servicereference = node.InnerText;
                                }
                                else if (node.Name == "e2servicename")
                                {
                                    e2servicename = node.InnerText;
                                }
                            }

                            // Check whether the current element is not just a label
                            if (!e2servicereference.StartsWith("1:64:"))
                            {
                                //check for radio channel
                                if (e2servicereference.Contains("radio"))
                                    channelInfo.ChannelType = ChannelType.Radio;
                                else
                                    channelInfo.ChannelType = ChannelType.TV;

                                channelInfo.HasImage = true;
                                channelInfo.Id = CreateEmbyChannelId(tuner, e2servicereference);

                                // image name is name is e2servicereference with last char removed, then replace all : with _, then add .png
                                var imageName = e2servicereference.Remove(e2servicereference.Length - 1);
                                imageName = imageName.Replace(":", "_");
                                imageName = imageName + ".png";
                                //var imageUrl = string.Format("{0}/picon/{1}", baseUrl, imageName);
                                var imageUrl = string.Format("{0}/picon/{1}", baseUrlPicon, imageName);

                                //channelInfo.ImageUrl = WebUtility.UrlEncode(imageUrl);
                                channelInfo.ImageUrl = imageUrl;

                                channelInfo.Name = e2servicename;
                                channelInfo.Number = count.ToString();

                                channelInfos.Add(channelInfo);
                                count = count + 1;
                            }
                            else
                            {
                                UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] ignoring channel {0}", e2servicereference));
                            }
                        }
                        return channelInfos;
                    }
                    catch (Exception e)
                    {
                        Logger.Error("[VuPlus] Failed to parse channel information.");
                        Logger.Error(string.Format("[VuPlus] GetChannelsForTVBouquetAsync error: {0}", e.Message));
                        throw new ApplicationException("Failed to parse channel information.");
                    }
                }
            }
        }

        private string GetStreamingBaseUrl(TunerHostInfo tuner, VuPlusTunerOptions config)
        {
            var baseUrl = tuner.Url.TrimEnd('/');

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
            {
                var builder = new UriBuilder(uri);
                builder.Port = config.StreamingPort;

                return builder.Uri.ToString().TrimEnd('/');
            }

            return baseUrl.TrimEnd('/');
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, BaseItem dbChannnel, ChannelInfo providerChannel, CancellationToken cancellationToken)
        {
            var config = GetProviderOptions<VuPlusTunerOptions>(tuner);

            Logger.Info("[VuPlus] Start GetChannelStream");

            var baseUrl = GetStreamingBaseUrl(tuner, config);

            var vuPlusChannelId = GetTunerChannelIdFromEmbyChannelId(tuner, providerChannel.Id);

            string streamUrl = string.Format("{0}/{1}", baseUrl, vuPlusChannelId);
            Logger.Info("[VuPlus] GetChannelStream url: {0}", streamUrl);

            var mediaSource = new MediaSourceInfo
            {
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        // Set the index to -1 because we don't know the exact index of the video stream within the container
                        Index = -1,
                        IsInterlaced = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        // Set the index to -1 because we don't know the exact index of the audio stream within the container
                        Index = -1
                    }
                },
                RequiresOpening = true,
                RequiresClosing = true,

                Id = streamUrl.GetMD5().ToString("N"),

                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsInfiniteStream = true
            };

            return Task.FromResult(new List<MediaSourceInfo>() { mediaSource });
        }

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return true;
        }

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(TunerHostInfo tuner, string tunerChannelId, DateTimeOffset startDateUtc, DateTimeOffset endDateUtc, CancellationToken cancellationToken)
        {
            var config = GetProviderOptions<VuPlusTunerOptions>(tuner);

            Logger.Info("[VuPlus] Start GetProgramsAsync");
            await EnsureConnectionAsync(tuner, config, cancellationToken).ConfigureAwait(false);

            var imagePath = "";
            var imageUrl = "";

            var baseUrl = tuner.Url.TrimEnd('/');

            var url = string.Format("{0}/web/epgservice?sRef={1}", baseUrl, tunerChannelId);
            Logger.Info("[VuPlus] GetProgramsAsync url: {0}", url);

            var options = new HttpRequestOptions()
            {
                CancellationToken = cancellationToken,
                Url = url
            };

            if (!string.IsNullOrEmpty(config.WebInterfaceUsername))
            {
                string authInfo = config.WebInterfaceUsername + ":" + config.WebInterfacePassword;
                authInfo = Convert.ToBase64String(Encoding.Default.GetBytes(authInfo));
                options.RequestHeaders["Authorization"] = "Basic " + authInfo;
            }

            using (var stream = await _httpClient.Get(options).ConfigureAwait(false))
            {
                using (StreamReader reader = new StreamReader(stream))
                {
                    string xmlResponse = reader.ReadToEnd();
                    UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetProgramsAsync response: {0}", xmlResponse));

                    try
                    {
                        var xml = new XmlDocument();
                        xml.LoadXml(xmlResponse);

                        List<ProgramInfo> programInfos = new List<ProgramInfo>();

                        int count = 1;

                        XmlNodeList e2event = xml.GetElementsByTagName("e2event");
                        foreach (XmlNode xmlNode in e2event)
                        {
                            var programInfo = new ProgramInfo();

                            var e2eventid = "?";
                            var e2eventstart = "?";
                            var e2eventduration = "?";
                            var e2eventcurrenttime = "?";
                            var e2eventtitle = "?";
                            var e2eventdescription = "?";
                            var e2eventdescriptionextended = "?";
                            var e2eventservicereference = "?";
                            var e2eventservicename = "?";

                            foreach (XmlNode node in xmlNode.ChildNodes)
                            {
                                if (node.Name == "e2eventid")
                                {
                                    e2eventid = node.InnerText;
                                }
                                else if (node.Name == "e2eventstart")
                                {
                                    e2eventstart = node.InnerText;
                                }
                                else if (node.Name == "e2eventduration")
                                {
                                    e2eventduration = node.InnerText;
                                }
                                else if (node.Name == "e2eventcurrenttime")
                                {
                                    e2eventcurrenttime = node.InnerText;
                                }
                                else if (node.Name == "e2eventtitle")
                                {
                                    e2eventtitle = node.InnerText;
                                }
                                else if (node.Name == "e2eventdescription")
                                {
                                    e2eventdescription = node.InnerText;
                                }
                                else if (node.Name == "e2eventdescriptionextended")
                                {
                                    e2eventdescriptionextended = node.InnerText;
                                }
                                else if (node.Name == "e2eventservicereference")
                                {
                                    e2eventservicereference = node.InnerText;
                                }
                                else if (node.Name == "e2eventservicename")
                                {
                                    e2eventservicename = node.InnerText;
                                }
                            }

                            long sdated = Int64.Parse(e2eventstart);
                            var sdate = DateTimeOffset.FromUnixTimeSeconds(sdated);

                            // Check whether the current element is within the time range passed
                            if (sdate > endDateUtc)
                            {
                                UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetProgramsAsync epc full ending without adding channel name : {0} program : {1}", e2eventservicename, e2eventtitle));
                                return programInfos;
                            }
                            else
                            {
                                UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetProgramsAsync adding program for channel name : {0} program : {1}", e2eventservicename, e2eventtitle));
                                //programInfo.HasImage = false;
                                //programInfo.ImagePath = null;
                                //programInfo.ImageUrl = null;
                                if (count == 1)
                                {
                                    //foreach (ChannelInfo channelInfo in tvChannelInfos)
                                    //{
                                    //    if (channelInfo.Name == e2eventservicename)
                                    //    {
                                    //        UtilsHelper.DebugInformation(Logger, string.Format("[VuPlus] GetProgramsAsync match on channel name : {0}", e2eventservicename));
                                    //        //programInfo.HasImage = true;
                                    //        //programInfo.ImagePath = channelInfo.ImagePath;
                                    //        //programInfo.ImageUrl = channelInfo.ImageUrl;
                                    //        imagePath = channelInfo.ImagePath;
                                    //        imageUrl = channelInfo.ImageUrl;
                                    //        break;
                                    //    }
                                    //}
                                }

                                programInfo.HasImage = true;
                                programInfo.ImagePath = imagePath;
                                programInfo.ImageUrl = imageUrl;

                                programInfo.ChannelId = tunerChannelId;

                                programInfo.Overview = e2eventdescriptionextended;

                                long edated = Int64.Parse(e2eventstart) + Int64.Parse(e2eventduration);
                                var edate = DateTimeOffset.FromUnixTimeSeconds(edated);

                                programInfo.StartDate = sdate.ToUniversalTime();
                                programInfo.EndDate = edate.ToUniversalTime();

                                programInfo.ShowId = e2eventid;
                                programInfo.Id = GetProgramEntryId(programInfo.ShowId, programInfo.StartDate, programInfo.ChannelId);

                                List<String> genre = new List<String>();
                                genre.Add("Unknown");
                                programInfo.Genres = genre;

                                //programInfo.OriginalAirDate = null;
                                programInfo.Name = e2eventtitle;
                                //programInfo.OfficialRating = null;
                                //programInfo.CommunityRating = null;
                                //programInfo.EpisodeTitle = null;
                                //programInfo.Audio = null;
                                //programInfo.IsHD = false;
                                //programInfo.IsRepeat = false;
                                //programInfo.IsSeries = false;
                                //programInfo.IsNews = false;
                                //programInfo.IsMovie = false;
                                //programInfo.IsKids = false;
                                //programInfo.IsSports = false;

                                programInfos.Add(programInfo);
                                count = count + 1;
                            }
                        }
                        return programInfos;
                    }
                    catch (Exception e)
                    {
                        Logger.Error("[VuPlus] Failed to parse program information.");
                        Logger.Error(string.Format("[VuPlus] GetProgramsAsync error: {0}", e.Message));
                        throw new ApplicationException("Failed to parse channel information.");
                    }
                }
            }
        }
    }
}
