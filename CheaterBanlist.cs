
using HarmonyLib;
using MelonLoader;
using Microsoft.SqlServer.Server;
using Steamworks;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
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
        // Ranks are corrected afterwards to ensure correct ordering

        // pagination is a little funny if there are lots of cheaters and you are moving right, there will be overlaps in the pages displayed
        // appears to work for global neon rankings
        // unsure if it interferes with friends leaderboard fix as I don't have enough steam friends on this game to fill the page


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

        // Rank count at which to calculate true rank (eg if we are in top 200, download ranks 1-200 and remove cheaters)
        // Increases load time if higher, plus we may get in trouble for too many requests to leaderboard API.
        public static int rankCount = 500;

        // stores fake/true ranks for remapping display
        public static Dictionary<int, int> ranksDict = new Dictionary<int, int>();


        public void Start()
        {
            StartCoroutine(DownloadCheaters());

            MethodInfo target = typeof(LeaderboardIntegrationSteam).GetMethod("GetScoreDataAtGlobalRank", BindingFlags.Static | BindingFlags.Public);
            HarmonyMethod postfix = new(typeof(CheaterBanlist).GetMethod("PreGetScoreDataAtGlobalRank"));
            NeonLite.Harmony.Patch(target, postfix);

            target = typeof(SteamUserStats).GetMethod("GetDownloadedLeaderboardEntry", BindingFlags.Static | BindingFlags.Public);
            postfix = new(typeof(CheaterBanlist).GetMethod("PostGetDownloadedLeaderboardEntry"));
            NeonLite.Harmony.Patch(target, null, postfix);

            target = typeof(LeaderboardScore).GetMethod("SetScore");
            HarmonyMethod prefix = new(typeof(CheaterBanlist).GetMethod("PreSetScore"));
            NeonLite.Harmony.Patch(target, prefix);

            target = typeof(Leaderboards).GetMethod("DisplayScores_AsyncRecieve");
            prefix = new(typeof(CheaterBanlist).GetMethod("PreDisplayScores_AsyncRecieve"));
            postfix = new(typeof(CheaterBanlist).GetMethod("PostDisplayScores_AsyncRecieve"));
            NeonLite.Harmony.Patch(target, prefix, postfix);

            target = typeof(SteamUserStats).GetMethod("DownloadLeaderboardEntries", BindingFlags.Static | BindingFlags.Public);
            postfix = new(typeof(CheaterBanlist).GetMethod("PreDownloadLeaderboardEntries"));
            NeonLite.Harmony.Patch(target, postfix);

            target = typeof(LeaderboardIntegrationSteam).GetMethod("OnLeaderboardScoreDownloadGlobalResult2", BindingFlags.Static | BindingFlags.Public);
            HarmonyMethod transpiler = new(typeof(CheaterBanlist).GetMethod("Transpiler"));
            NeonLite.Harmony.Patch(target, null, null, transpiler);
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

        public static void PreDownloadLeaderboardEntries(SteamLeaderboard_t hSteamLeaderboard, ELeaderboardDataRequest eLeaderboardDataRequest, ref int nRangeStart, ref int nRangeEnd)
        {
            rankStart = nRangeStart; // highest rank on page requested
            if (rankStart < rankCount && eLeaderboardDataRequest != ELeaderboardDataRequest.k_ELeaderboardDataRequestFriends)
            {
                nRangeStart = 0;
                nRangeEnd = rankCount;
            }
        }

        public static void PreSetScore(ref ScoreData newData, ref bool globalNeonRankings)
        {
            newData._ranking = ranksDict[newData._ranking];
        }

        public static void PostDisplayScores_AsyncRecieve()
        {
            cheaters.Clear();
            ranksDict.Clear();
        }


        public static void PreDisplayScores_AsyncRecieve(ref ScoreData[] scoreDatas)
        {
            // if we are in the top rankCount ranks, recalculate and return first 10 non cheaters higher than starting rank
            // if only scoreDatas.Length is 10 or less then this is a friends page so don't do anything
            if (rankStart < rankCount && scoreDatas.Length > 10)
            {
                ScoreData[] resultScoreDatas = new ScoreData[10];
                int fixedRank = 0;
                int j = 0;

                for (int i = 0; i < scoreDatas.Length; i++)
                {
                    if (!cheaters.Contains(scoreDatas[i]._ranking))
                    {
                        fixedRank += 1;
                        if (scoreDatas[i]._ranking >= rankStart)
                        {
                            // store fixed ranks in dict to apply after setscore is called
                            ranksDict.Add(scoreDatas[i]._ranking, fixedRank);
                            resultScoreDatas[j] = scoreDatas[i];
                            j += 1;
                        }
                    }
                    if (j == 10) break;
                }
                scoreDatas = resultScoreDatas;
            }
            else
            {
                for (int i = 0; i < 10; i++)
                {
                    ranksDict.Add(scoreDatas[i]._ranking, scoreDatas[i]._ranking);
                }
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
