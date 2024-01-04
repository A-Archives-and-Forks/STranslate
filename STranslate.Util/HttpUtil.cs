using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STranslate.Util
{
    public class HttpUtil
    {
        /// <summary>
        /// �첽Get����(����Token)
        /// </summary>
        /// <param name="url"></param>
        /// <param name="req"></param>
        /// <returns></returns>
        public static async Task<string> GetAsync(string url, int timeout = 10) => await GetAsync(url, CancellationToken.None, timeout);

        /// <summary>
        /// �첽Get����
        /// </summary>
        /// <param name="url"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public static async Task<string> GetAsync(string url, CancellationToken token, int timeout = 10)
        {
            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(timeout),
            };

            try
            {
                var respContent = await client.GetAsync(url, token);

                string respStr = await respContent.Content.ReadAsStringAsync(token);

                return respStr;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// �첽Get���󣬴���ѯ����
        /// </summary>
        /// <param name="url">�����URL</param>
        /// <param name="queryParams">��ѯ�����ֵ�</param>
        /// <param name="token">ȡ������</param>
        /// <param name="timeout">��ʱʱ�䣨�룩</param>
        /// <returns></returns>
        public static async Task<string> GetAsync(string url, Dictionary<string, string> queryParams, CancellationToken token, int timeout = 10)
        {
            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(timeout),
            };

            try
            {
                // ��������ѯ������URL
                if (queryParams != null && queryParams.Count > 0)
                {
                    var queryBuilder = new StringBuilder();
                    foreach (var kvp in queryParams)
                    {
                        queryBuilder.Append(Uri.EscapeDataString(kvp.Key));
                        queryBuilder.Append("=");
                        queryBuilder.Append(Uri.EscapeDataString(kvp.Value));
                        queryBuilder.Append("&");
                    }

                    string queryString = queryBuilder.ToString().TrimEnd('&');
                    url += "?" + queryString;
                }

                var respContent = await client.GetAsync(url, token);

                string respStr = await respContent.Content.ReadAsStringAsync(token);

                return respStr;
            }
            catch (Exception)
            {
                throw;
            }
        }


        /// <summary>
        /// �첽Post����(����Token)
        /// </summary>
        /// <param name="url"></param>
        /// <param name="req"></param>
        /// <returns></returns>
        public static async Task<string> PostAsync(string url, string req, int timeout = 10) => await PostAsync(url, req, CancellationToken.None, timeout);

        /// <summary>
        /// �첽Post����
        /// </summary>
        /// <param name="url"></param>
        /// <param name="req"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public static async Task<string> PostAsync(string url, string req, CancellationToken token, int timeout = 10)
        {
            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(timeout),
            };

            try
            {
                var content = new StringContent(req, Encoding.UTF8, "application/json");

                var respContent = await client.PostAsync(url, content, token);

                string respStr = await respContent.Content.ReadAsStringAsync(token);

                return respStr;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>
        /// ֧��ϵͳ����
        /// </summary>
        public static void SupportSystemAgent()
        {
            WebRequest.DefaultWebProxy = WebRequest.GetSystemWebProxy();
            WebRequest.DefaultWebProxy.Credentials = CredentialCache.DefaultCredentials;
        }
        //TODO: ���һ��ʼû�п�ϵͳ��������������ٴ�ϵͳ������Ȼ�������
        //LogService.Logger.Info("START");
        //var ret = await HttpUtil.GetAsync("https://rsshub.zggsong.workers.dev/", timeout: 10);
        //LogService.Logger.Info(ret);
        //LogService.Logger.Info("END");
        //return;
    }
}
