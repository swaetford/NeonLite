
using HarmonyLib;
using MelonLoader;
using Microsoft.SqlServer.Server;
using Steamworks;
using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.SocialPlatforms;
using UnityEngine.SocialPlatforms.Impl;
using static MelonLoader.MelonLogger;
using static UnityEngine.Rendering.DebugUI;
using Debug = UnityEngine.Debug;

namespace NeonWhiteQoL
{
    public class CheaterBanlist : MonoBehaviour
    {
        // These changes allow for a global leaderboard display without any cheaters, for players at the top of the leaderboard
        // rankCount specifies the cutoff point, longer times will introduce a larger delay when cycling through pages + it hits the api harder
        // I have been testing with 500, the delay is fine for me and my country doesn't have any steam servers, YMMV


        // Eg. rankCount = 500.
        // If a player is within top 500 on steam leaderboard, it downloads all scores from rank 1 to 500, filters through the results and removes cheaters and shows true ranks
        // Does introduce a gap when you are going down the leaderboard and it cuts from removing cheaters to showing ranks normally

        // The OnLeaderboardScoreDownloadGlobalResult2 method is patched with a transpiler so it will download as many scores as requested instead of the hardcoded 10

        // If this happens then a prefix is applied to DisplayScores_AsyncRecieve which removes the cheaters and gets the correct entries to display
        // Ranks are corrected afterwards to ensure highestRanking is set correctly

        // pagination is a little funny if there are lots of cheaters, the pages will overlap.
        // theres probably a way to fix this by patching the page methods as in the leaderboard fix

        // btw unsure if it interferes with friends leaderboard fix as I don't have enough steam friends playing this game to fill the page ...
        // I think it works for global neon rankings but im not ranked in top 500 so can't check properly

        // the private static attribute below is used for debugging purposes and getting steamids lol
        private static FieldInfo currentLeaderboardEntriesGlobal = typeof(LeaderboardIntegrationSteam).GetField("currentLeaderboardEntriesGlobal", BindingFlags.NonPublic | BindingFlags.Static);
        public static bool isLoaded = false;
        public static bool? friendsOnly = null;
        public static int globalRank;
        public static List<int> cheaters = new();
        public static ulong[] bannedIDs;
        public static string test = string.Empty;

        // used to store top rank requested for a page
        public static int rankStart = 0;

        // Rank count at which to calculate true rank (eg if we are in top 500, download ranks 1-500 and remove cheaters)
        // Increases load time if higher, plus we may get in trouble for too many requests to leaderboard API.
        // must be greater than 10
        public static int rankCount = 500;

        // stores fake/true ranks for remapping display
        public static Dictionary<int, int> ranksDict = new Dictionary<int, int>();

        public static ScoreData[] scoreDataCache;
        public static SteamLeaderboard_t cachedSteamLeaderboard_t;
        public static SteamLeaderboard_t newSteamLeaderboard_t;
        public static int highestRankCached = rankCount;

        private static readonly FieldInfo _leaderboardsRefInfo = typeof(LeaderboardIntegrationSteam).GetField("leaderboardsRef", BindingFlags.NonPublic | BindingFlags.Static);


        public void Start()
        {
            StartCoroutine(DownloadCheaters());

            MethodInfo target = typeof(LeaderboardIntegrationSteam).GetMethod("GetScoreDataAtGlobalRank", BindingFlags.Static | BindingFlags.Public);
            HarmonyMethod postfix = new(typeof(CheaterBanlist).GetMethod("PreGetScoreDataAtGlobalRank"));
            NeonLite.Harmony.Patch(target, postfix);

            target = typeof(SteamUserStats).GetMethod("GetDownloadedLeaderboardEntry", BindingFlags.Static | BindingFlags.Public);
            postfix = new(typeof(CheaterBanlist).GetMethod("PostGetDownloadedLeaderboardEntry"));
            NeonLite.Harmony.Patch(target, null, postfix);

            //target = typeof(LeaderboardScore).GetMethod("SetScore");
            //HarmonyMethod prefix = new(typeof(CheaterBanlist).GetMethod("PreSetScore"));
            //NeonLite.Harmony.Patch(target, prefix);

            target = typeof(Leaderboards).GetMethod("DisplayScores_AsyncRecieve");
            HarmonyMethod prefix = new(typeof(CheaterBanlist).GetMethod("PreDisplayScores_AsyncRecieve"));
            postfix = new(typeof(CheaterBanlist).GetMethod("PostDisplayScores_AsyncRecieve"));
            NeonLite.Harmony.Patch(target, prefix, postfix);

            target = typeof(LeaderboardIntegrationSteam).GetMethod("OnLeaderboardScoreDownloadGlobalResult2", BindingFlags.Static | BindingFlags.Public);
            HarmonyMethod transpiler = new(typeof(CheaterBanlist).GetMethod("Transpiler"));
            NeonLite.Harmony.Patch(target, null, null, transpiler);

            target = typeof(SteamUserStats).GetMethod("DownloadLeaderboardEntries", BindingFlags.Static | BindingFlags.Public);
            prefix = new(typeof(CheaterBanlist).GetMethod("PreDownloadLeaderboardEntries"));
            NeonLite.Harmony.Patch(target, prefix);

            
        }

        public IEnumerator DownloadCheaters()
        {
            using (UnityWebRequest webRequest = UnityWebRequest.Get("https://raw.githubusercontent.com/Faustas156/NeonLiteBanList/main/banlist.txt"))
            {
                yield return webRequest.SendWebRequest();

                switch (webRequest.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                    case UnityWebRequest.Result.DataProcessingError:
                        Debug.LogError("Error: " + webRequest.error);
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        Debug.LogError("HTTP Error: " + webRequest.error);
                        break;
                    case UnityWebRequest.Result.Success:
                        test = webRequest.downloadHandler.text;
                        string[] downloadedCheaters = webRequest.downloadHandler.text.Split();
                        bannedIDs = new ulong[downloadedCheaters.Length];
                        for (int i = 0; i < downloadedCheaters.Length; i++)
                        {
                            bannedIDs[i] = ulong.Parse(GetNumbers(downloadedCheaters[i]));
                        }
                        isLoaded = true;
                        break;
                }
            }
        }
        private static string GetNumbers(string input)
        {
            return new string(input.Where(c => char.IsDigit(c)).ToArray());
        }

        public static void PreGetScoreDataAtGlobalRank(ref int globalRank, ref bool friendsOnly, ref bool globalNeonRanking)
        {
            CheaterBanlist.friendsOnly = friendsOnly;
            CheaterBanlist.globalRank = globalRank;
        }


        public static void PostGetDownloadedLeaderboardEntry(ref SteamLeaderboardEntries_t hSteamLeaderboardEntries, ref int index, ref LeaderboardEntry_t pLeaderboardEntry, ref int[] pDetails, ref int cDetailsMax, ref bool __result)
        {
            if (friendsOnly != null && pLeaderboardEntry.m_steamIDUser.m_SteamID != 0 && bannedIDs.Contains(pLeaderboardEntry.m_steamIDUser.m_SteamID))
            {
                cheaters.Add((bool)friendsOnly ? globalRank : pLeaderboardEntry.m_nGlobalRank);
            }
            friendsOnly = null;
        }
        public static bool PreDownloadLeaderboardEntries(SteamLeaderboard_t hSteamLeaderboard, ELeaderboardDataRequest eLeaderboardDataRequest, ref int nRangeStart, ref int nRangeEnd)
        {

            Leaderboards leaderboard = (Leaderboards)_leaderboardsRefInfo.GetValue(null);
            Melon<NeonLite>.Logger.Msg("leaderboard request " + hSteamLeaderboard.m_SteamLeaderboard.ToString() +" start " + nRangeStart.ToString() + " end " + nRangeEnd.ToString());
            if (eLeaderboardDataRequest == ELeaderboardDataRequest.k_ELeaderboardDataRequestFriends) return true;
            else
            {
                newSteamLeaderboard_t = hSteamLeaderboard;
                rankStart = nRangeStart;
                if (cachedSteamLeaderboard_t.m_SteamLeaderboard != newSteamLeaderboard_t.m_SteamLeaderboard)
                {
                    Melon<NeonLite>.Logger.Msg("Doesn't match cache" );
                    // if in top ranks and not getting data for friends leaderboard increase count requested
                    if (rankStart < rankCount)
                    {
                        Melon<NeonLite>.Logger.Msg("In top 500");
                        nRangeStart = 0;
                        nRangeEnd = rankCount;
                    }
                    return true;
                }
                else if (rankStart > rankCount)
                {
                    Melon<NeonLite>.Logger.Msg("Matches cache but above top 500, use requested value");
                    return true;
                }
                else if (rankStart > scoreDataCache.Length)
                {
                    Melon<NeonLite>.Logger.Msg("Matches cache but is above highest cached rank " + highestRankCached.ToString());
                    nRangeStart = highestRankCached + 1;
                    nRangeEnd = nRangeStart + 9;
                    return true;
                }
                else
                {
                    Melon<NeonLite>.Logger.Msg("Using cache of size" + scoreDataCache.Length.ToString());

                    var selectedScoreDatas = new ScoreData[10];
                    Array.Copy(scoreDataCache, rankStart - 1, selectedScoreDatas, 0, Math.Min(10, scoreDataCache.Length - rankStart));
                    leaderboard.DisplayScores_AsyncRecieve(selectedScoreDatas, true);
                    return false;
                }
            }
        }

        public static void PostDisplayScores_AsyncRecieve()
        {
            cheaters.Clear();
            ranksDict.Clear();
        }


        public static void PreDisplayScores_AsyncRecieve(ref ScoreData[] scoreDatas)
        {
            if (scoreDatas.Length > 10)
            {


                ScoreData[] resultScoreDatas = scoreDatas.Where(x => !cheaters.Contains(x._ranking)).ToArray();

                int fixedRank = 1;
                bool foundUser = false;
                bool foundNewStartRank = false;
                highestRankCached = resultScoreDatas.Last()._ranking;
                for (int i = 0; i < resultScoreDatas.Length; i++)
                {
                    if (!foundNewStartRank)
                    {
                        if(resultScoreDatas[i]._ranking == rankStart)
                        {
                            rankStart = fixedRank;
                            foundNewStartRank = true;
                        }
                    }
                    resultScoreDatas[i]._ranking = fixedRank;
                    if (!foundUser)
                    {
                        if (resultScoreDatas[i]._userScore)
                        {
                            Leaderboards leaderboard = (Leaderboards)_leaderboardsRefInfo.GetValue(null);
                            leaderboard.SetUserRanking(fixedRank);
                            foundUser = true;
                        }
                    }
                    fixedRank += 1;
                }

                scoreDataCache = resultScoreDatas;
                cachedSteamLeaderboard_t = newSteamLeaderboard_t;
                var selectedScoreDatas = new ScoreData[10];
                Array.Copy(scoreDataCache, rankStart - 1, selectedScoreDatas,0, Math.Min(10, scoreDataCache.Length-rankStart));
                scoreDatas = selectedScoreDatas;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (var i = 0; i < codes.Count; i++)
            {
                // run through instructions and replace hardcoded request count - there is only one opcode in the the method to search for
                if (codes[i].opcode == OpCodes.Ldc_I4_S) // original instruction loads int8 value (operand 10) onto stack (for length of scoredata)
                {
                    codes[i].opcode = OpCodes.Ldarg_0; // load argument LeaderboardScoresDownloaded_t onto stack
                    codes.Insert(
                        i + 1, 
                        new CodeInstruction(
                            opcode: OpCodes.Ldfld, // load field m_cEntryCount from this onto stack
                            operand: typeof(LeaderboardScoresDownloaded_t).GetField("m_cEntryCount", BindingFlags.Public | BindingFlags.Instance)
                            )
                        );
                    break;
                }
            }
            return codes.AsEnumerable();
        }

    }
}
