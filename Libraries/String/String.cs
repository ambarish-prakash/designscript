using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace String
{
    public class String
    {

        public static int StringLength(string str)
        {
            return str.Length;
        }

        public static string ToUpper(string str)
        {
            return str.ToUpper();
        }

        public static string ToLower(string str)
        {
            return str.ToLower();
        }

        public static double ToNumber(string str)
        {
            return Convert.ToDouble(str);
        }

        public static List<string> SplitString(string str, string delimiter)
        {
            string[] strArray = { delimiter };
            string[] resultArray = str.Split(strArray, System.StringSplitOptions.RemoveEmptyEntries);
            return resultArray.ToList();
        }

        public static string JoinStrings(string[] str, string delimiter)
        {
            if (str == null)
                return "";
            return string.Join(delimiter, str);
        }

        public static string JoinStrings(string[] str)
        {
            return JoinStrings(str, "");
        }

        public static string SubString(string str, int start, int length)
        {
            return str.Substring(start, length);
        }

        public static string NumberToString(double num)
        {
            return num.ToString();
        }

        public static List<double> NumberSequence(double start, int amount, double step)
        {
            double number = start;
            List<double> result = new List<double>();
            for (int i = 0; i < amount; i++)
            {
                result.Add(number);
                number += step;
            }
            return result;
        }

        public static List<object> ShiftList(List<object> list, int amount)
        {
            if (amount == 0)
                return list;

            if (amount < 0)
            {
                return list.Skip(-amount).Concat(list.Take(-amount)).ToList();
            }

            int len = list.Count;
            return list.Skip(len - amount).Concat(list.Take(len - amount)).ToList();
        }

        public static List<object> SliceList(List<object> list, int start, int count)
        {
            return list.Skip(start).Take(count).ToList();
        }

        public static List<List<object>> Slice(List<object> list, int n)
        {
            List<List<object>> result = new List<List<object>>();
            List<object> subList = new List<object>();
            int count=0;
            for (int i = 0; i < list.Count; i++)
            {
                count++;
                subList.Add(list.ElementAt(i));

                if (count == n)
                {
                    result.Add(subList);
                    count = 0;
                    subList = new List<object>();
                }
            }
            if (subList.Count != 0)
                result.Add(subList);
            
            return result;
        }

        public static List<object> DropList(List<object> list, int amount)
        {
            return list.Skip(amount).ToList();
        }

        public static List<object> RemoveEveryNth(List<object> list, int n, int offset)
        {
            return list.Skip(offset).Where((_, i) => (i + 1) % n != 0).ToList();
        }

        public static List<List<object>> DiagonalLeftList(List<object> list, int n)
        {
            List<List<object>> result = new List<List<object>>();
            if (n > list.Count)
            {
                result.Add(list);
                return result;
            }
            List<object> currList = new List<object>();
            List<int> startIndices = new List<int>();

            for (int i = 0; i < n; i++)
            {
                startIndices.Add(i);
            }

            for (int i = n - 1 + n; i < list.Count(); i += n)
            {
                startIndices.Add(i);
            }

            foreach (int start in startIndices)
            {
                int index = start;

                while (index < list.Count)
                {
                    var currentRow = (int)Math.Ceiling((index + 1) / (double)n);
                    currList.Add(list.ElementAt(index));
                    index += n - 1;

                    //ensure we are skipping a row to get the next index
                    var nextRow = (int)Math.Ceiling((index + 1) / (double)n);
                    if (nextRow > currentRow + 1 || nextRow == currentRow)
                        break;
                }
                result.Add(currList);
                currList = new List<object>();
            }

            if (currList.Count!=0)
            {
                result.Add(currList);
            }

            return result;
        }


        public static List<List<object>> DiagonalRightList(List<object> list, int n)
        {
            List<List<object>> result = new List<List<object>>();
            if (n > list.Count)
            {
                result.Add(list);
                return result;
            }
            List<object> currList = new List<object>();
            List<int> startIndices = new List<int>();

            for (int i = n; i < list.Count(); i += n)
            {
                startIndices.Add(i);
            }

            startIndices.Reverse();

            for (int i = 0; i < n; i++)
            {
                startIndices.Add(i);
            }

            foreach (int start in startIndices)
            {
                int index = start;

                while (index < list.Count)
                {
                    var currentRow = (int)Math.Ceiling((index + 1) / (double)n);
                    currList.Add(list.ElementAt(index));
                    index += n + 1;

                    //ensure we are skipping a row to get the next index
                    var nextRow = (int)Math.Ceiling((index + 1) / (double)n);
                    if (nextRow > currentRow + 1 || nextRow == currentRow)
                        break;
                }
                result.Add(currList);
                currList = new List<object>();
            }

            if (currList.Count != 0)
            {
                result.Add(currList);
            }

            return result;
        }

        public static double RandomSeed(int seed)
        {
            System.Random rand = new System.Random(seed);
            return rand.NextDouble();
        }

        public static List<object> Repeat(object item, int count)
        {
            List<object> result = new List<object>();
            if (count < 0)
                return result;
            result = Enumerable.Repeat(item, count).ToList();
            return result;
        }

    }
}