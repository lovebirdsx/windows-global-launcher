using System;

namespace CommandLauncher
{
    // 模糊匹配服务
    public class FuzzyMatcher
    {
        public static double GetMatchScore(string query, string target)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target))
                return 0;

            query = query.ToLower();
            target = target.ToLower();

            // 完全匹配得分最高
            if (target.Contains(query))
            {
                return 1.0 - (double)(target.Length - query.Length) / target.Length;
            }

            // 计算字符匹配度
            int matches = 0;
            int queryIndex = 0;

            for (int i = 0; i < target.Length && queryIndex < query.Length; i++)
            {
                if (target[i] == query[queryIndex])
                {
                    matches++;
                    queryIndex++;
                }
            }

            return queryIndex == query.Length ? (double)matches / target.Length * 0.8 : 0;
        }

        public static double GetCommandMatchScore(string query, Command command)
        {
            var nameScore = GetMatchScore(query, command.Name);
            var descScore = GetMatchScore(query, command.Description);
            var shellScore = GetMatchScore(query, command.Shell);

            return Math.Max(nameScore, Math.Max(descScore, shellScore));
        }
    }
}
