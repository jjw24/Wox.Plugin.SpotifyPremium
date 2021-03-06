﻿using Flow.Launcher.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Wox.Plugin.SpotifyPremium
{
    public class SpotifyPlugin : IPlugin
    {
        private PluginInitContext _context;

        private SpotifyApi _api;

        private readonly Dictionary<string, Func<string, List<Result>>> _terms = new Dictionary<string, Func<string, List<Result>>>();

        private const string SpotifyIcon = "icon.png";

        private string currentUserId; //Required for playlist querying

        private bool optimizeApiUsage = true;   //Flag to limit API calls to X ms after a keystroke 
                                                //Set to 'false' to stop optimizing APi calls

        private DateTime lastQueryTime; //Record the time on every query
                                        //Almost every keypress counts as a new query

        private int optimzeApiKeyDelay = 500; //Time to wait before issuing an expensive query
        private int cachedVolume = -1;

        
        private String[] expensiveSearchTerms = {"artist","album","track","playlist"};  //Specify expensive search terms for optimizing api usage
                                                                                        //Wait for delay before querying 

        public void Init(PluginInitContext context)
        {
            _context = context;
            lastQueryTime = DateTime.UtcNow;

            // initialize data, passing it the plugin directory
            Task.Run(() => _api = new SpotifyApi(_context.CurrentPluginMetadata.PluginDirectory));

            _terms.Add("artist", SearchArtist);
            _terms.Add("album", SearchAlbum);
            _terms.Add("playlist", SearchPlaylist);
            _terms.Add("track", SearchTrack);
            _terms.Add("next", PlayNext);
	        _terms.Add("last", PlayLast);
            _terms.Add("pause", Pause);
            _terms.Add("play", Play);
            _terms.Add("mute", ToggleMute);
            _terms.Add("vol", SetVolume);
            _terms.Add("volume", SetVolume);
            _terms.Add("device", GetDevices);
            _terms.Add("shuffle", ToggleShuffle);

            //view query count and average query duration
            _terms.Add("diag", q =>
                SingleResult($"Query Count: {context.CurrentPluginMetadata.QueryCount}",
                $"Avg. Query Time: {context.CurrentPluginMetadata.AvgQueryTime}ms",
                null));

            _terms.Add("reconnect", q =>
                SingleResult("Reconnect","Force a reconnection and remove the refresh token",reconnectAction(_api, false))
                );
        }

        private List<Result> Play(string arg) =>
            SingleResult("Play", $"Resume: {_api.PlaybackContext.Item.Name}", _api.Play);

        private List<Result> Pause(string arg = null) =>
            SingleResult("Pause", $"Pause: {_api.PlaybackContext.Item.Name}", _api.Pause);

        private List<Result> PlayNext(string arg) =>
            SingleResult("Next", $"Skip: {_api.PlaybackContext.Item.Name}", _api.Skip);

        private List<Result> PlayLast(string arg) =>
            SingleResult("Last", "Skip Backwards", _api.SkipBack);

        public List<Result> Query(Query query)
        {
            //Record the time the query was issued
            lastQueryTime = DateTime.UtcNow;
            DateTime thisQueryStartTime = DateTime.UtcNow;

            if (!_api.ApiConnected)
            {
                return SingleResult("Spotify API unreachable", "Select to re-authorize", reconnectAction(_api));
            }

            if (!_api.TokenValid)
            {
                return SingleResult("Spotify API Token Expired", "Select to re-authorize", reconnectAction(_api));
            }

            try
            {
                // display status if no parameters are added
                if (string.IsNullOrWhiteSpace(query.Search))
                {
                    return GetPlaying();
                }
                
                //Run the query if it is not an expensive search term
                if(_terms.ContainsKey(query.FirstSearch) && !expensiveSearchTerms.Contains(query.FirstSearch)){
                    var results = _terms[query.FirstSearch].Invoke(query.SecondToEndSearch);
                    return results;                    
                }

                //If query is expensive, AND if optimizeApiUsage is flagged
                //  return null if query is updated within set number ms
                //  this limits the API calls made
                //  if you type a 10 character query quickly enough, only the last keypress searches the Spotify API
                if(optimizeApiUsage){
                    System.Threading.Thread.Sleep(optimzeApiKeyDelay);
                    if(lastQueryTime > thisQueryStartTime){
                        return null;
                    }
                }

                if (_terms.ContainsKey(query.FirstSearch))
                {
                    var results = _terms[query.FirstSearch].Invoke(query.SecondToEndSearch);
                    return results;
                }

                return SearchAll(query.Search);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            //If searches run into an exception, return results not found Result
            return NothingFoundResult; 
        }

        private List<Result> GetPlaying()
        {
            var d = _api.ActiveDeviceName;
            if (d == null)
            {
                //Must have an active device to control Spotify
                return SingleResult("No active device","Select device with `sp device`",()=>{});
            }

            var t = _api.PlaybackContext.Item;
            if (t == null)
            {
                return SingleResult("No track playing",$"Active Device: {d}",()=>{});
            }

            var status = _api.PlaybackContext.IsPlaying ? "Now Playing" : "Paused";
            var toggleAction = _api.PlaybackContext.IsPlaying ? "Pause" : "Resume";
            var icon = _api.GetArtworkAsync(t);
            icon.Wait();

            return new List<Result>()
            {
                new Result()
                {
                    Title = t.Name,
                    SubTitle = $"{status} | by {String.Join(", ",t.Artists.Select(a => String.Join("",a.Name)))}",
                    IcoPath = icon.Result
                },
                new Result()
                {
                    IcoPath = SpotifyIcon,
                    Title = "Pause / Resume",
                    SubTitle = $"{toggleAction}: {t.Name}",
                    Action = _ =>
                    {
                        if (_api.PlaybackContext.IsPlaying)
                            _api.Pause();
                        else
                            _api.Play();
                        return true;
                    }
                },
                new Result()
                {
                    IcoPath = SpotifyIcon,
                    Title = "Next",
                    SubTitle = $"Skip: {t.Name}",
                    Action = context =>
                    {
                        _api.Skip();
                        return true;
                    }
                },
                new Result()
                {
                    IcoPath = SpotifyIcon,
                    Title = "Last",
                    SubTitle = "Skip backwards",
                    Action = context =>
                    {
                        _api.SkipBack();
                        return true;
                    }
                },
                ToggleMute().First(),
                ToggleShuffle().First(),
                SetVolume().First()
            };
        }

        private List<Result> ToggleMute(string arg = null)
        {
            var toggleAction = _api.MuteStatus ? "Unmute" : "Mute";

            return SingleResult("Toggle Mute", $"{toggleAction}: {_api.PlaybackContext.Item.Name}", _api.ToggleMute);
        }

        private List<Result> SetVolume(string arg = null)
        {
            if (Int32.TryParse(arg, out int tempInt)){
                if (tempInt >= 0 && tempInt <= 100){
                    return SingleResult($"Set Volume to {tempInt}",$"Current Volume: {cachedVolume}", ()=>{
                        _api.SetVolume(tempInt);
                        });
                }
            }

            cachedVolume = _api.CurrentVolume;
            return SingleResult($"Volume", $"Current Volume: {cachedVolume}", ()=>{});
        }

        private List<Result> ToggleShuffle(string arg = null)
        {
            var toggleAction = _api.ShuffleStatus ? "Off" : "On";

            return SingleResult("Toggle Shuffle", $"Turn Shuffle {toggleAction}", _api.ToggleShuffle);
        }

        private List<Result> SearchAll(string param)
        {
            if (!_api.ApiConnected) return AuthenticateResult;

            if (string.IsNullOrWhiteSpace(param))
            {
                return new List<Result>();
            }

            // Retrieve data and return the first 20 results
            var results = _api.SearchAll(param).Select(async x => new Result()
            {
                Title = x.Title,
                SubTitle = x.Subtitle,
                IcoPath = await _api.GetArtworkAsync(x),
                Action = _ =>
                {
                    _api.Play(x.Uri);
                    return true;
                }
            }).ToArray();

            Task.WaitAll(results);
            return (results.Count() > 0) ? results.Select(x => x.Result).ToList() : NothingFoundResult;
        }

        private List<Result> SearchTrack(string param)
        {
            if (!_api.ApiConnected) return AuthenticateResult;

            if (string.IsNullOrWhiteSpace(param))
            {
                return new List<Result>();
            }

            // Retrieve data and return the first 20 results
            var results = _api.GetTracks(param).Select(async x => new Result()
            {
                Title = x.Name,
                SubTitle = "Artist: " + string.Join(", ", x.Artists.Select(a => a.Name)),
                IcoPath = await _api.GetArtworkAsync(x),
                Action = _ =>
                {
                    _api.Play(x.Uri);
                    return true;
                }
            }).ToArray();

            Task.WaitAll(results);
            return (results.Count() > 0) ? results.Select(x => x.Result).ToList() : NothingFoundResult;
        }

        private List<Result> SearchAlbum(string param)
        {
            if (!_api.ApiConnected) return AuthenticateResult;

            if (string.IsNullOrWhiteSpace(param))
            {
                return new List<Result>();
            }

            // Retrieve data and return the first 10 results
            var results = _api.GetAlbums(param).Select(async x => new Result()
            {
                Title = x.Name,
                SubTitle = "by " + string.Join(", ", x.Artists.Select(a => a.Name)),
                IcoPath = await _api.GetArtworkAsync(x),
                Action = _ =>
                {
                    _api.Play(x.Uri);
                    return true;
                }                
            }).ToArray();

            Task.WaitAll(results);
            return (results.Count() > 0) ? results.Select(x => x.Result).ToList() : NothingFoundResult;
        }

        private List<Result> SearchArtist(string param)
        {
            if (!_api.ApiConnected) return AuthenticateResult;

            if (string.IsNullOrWhiteSpace(param))
            {
                return new List<Result>();
            }

            // Retrieve data and return the first 10 results
            var results = _api.GetArtists(param).Select(async x => new Result()
            {
                Title = x.Name,
                SubTitle = $"Popularity: {x.Popularity}%",
                IcoPath = await _api.GetArtworkAsync(x),
                // When selected, open it with the spotify client
                Action = _ =>
                {
                    _api.Play(x.Uri);
                    return true;
                }
            }).ToArray();

            Task.WaitAll(results);
            return (results.Count() > 0) ? results.Select(x => x.Result).ToList() : NothingFoundResult;
        }
        private List<Result> SearchPlaylist(string param)
        {
            if (!_api.ApiConnected) return AuthenticateResult;

            if (string.IsNullOrWhiteSpace(param))
            {
                param = "";
            }

            // Retrieve data and return the first 500 playlists
            var results = _api.GetPlaylists(param,currentUserId).Select(async x => new Result()
            {
                Title = x.Name,
                SubTitle = x.Type,
                IcoPath = await _api.GetArtworkAsync(x),
                Action = _ =>
                {
                    _api.Play(x.Uri);
                    return true;
                }                
            }).ToArray();

            Task.WaitAll(results);
            return (results.Count() > 0) ? results.Select(x => x.Result).ToList() : NothingFoundResult;
        }

        private List<Result> GetDevices(string param = null)
        {
            //Retrieve all available devices
            List<SpotifyAPI.Web.Models.Device> allDevices = _api.GetDevices();
            if (allDevices == null || allDevices.Count == 0) return SingleResult("No devices found on Spotify.","Reconnect to API",reconnectAction(_api));

            var results = _api.GetDevices().Where( device => !device.IsRestricted).Select(async x => new Result()
            {
                Title = $"{x.Type}  {x.Name}",
                SubTitle = x.IsActive ? "Active Device" : "Inactive",
                //TODO: Add computer and phone icons
                //IcoPath = await _api.GetArtworkAsync(x.Images,x.Uri),
                Action = _ =>
                {
                    _api.SetDevice(x.Id);
                    return true;
                }                
            }).ToArray();

            Task.WaitAll(results);
            return results.Select(x => x.Result).ToList();
        }
        
        //Return a generic reconnection action
        private Action reconnectAction(SpotifyApi api, bool keepRefreshToken = true){
            return () =>
            {
                Task connectTask = api.ConnectWebApi(keepRefreshToken);
                //Assign client ID asynchronously when connection finishes
                connectTask.ContinueWith((connectResult) => { 
                    try{
                        currentUserId = api.GetUserID();
                    }
                    catch{
                        Console.WriteLine("Failed to write client ID");
                    }
                });
            };
        }

        private List<Result> AuthenticateResult =>
            SingleResult("Authentication required to search the Spotify library", "Click this to authenticate", reconnectAction(_api));



        // Returns a SingleResult if no search results are found
        private List<Result> NothingFoundResult =>
            SingleResult("No results found on Spotify.", "Please try refining your search", () => {});
            
        // Returns a list with a single result
        private List<Result> SingleResult(string title, string subtitle = "", Action action = default(Action)) =>
            new List<Result>()
            {
                new Result()
                {
                    Title = title,
                    SubTitle = subtitle,
                    IcoPath = SpotifyIcon,
                    Action = _ =>
                    {
                        action();
                        return true;
                    }
                }
            }; 
    }
}
