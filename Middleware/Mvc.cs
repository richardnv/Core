﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Datasilk.Core.Middleware
{
    public class Mvc
    {
        private readonly RequestDelegate _next;
        private readonly MvcOptions _options;
        private int requestCount = 0;
        private Web.Routes routes;
        private Dictionary<string, Type> controllers = new Dictionary<string, Type>();
        private Dictionary<string, Type> services = new Dictionary<string, Type>();
        private Dictionary<string, string> controllerNamespaces = new Dictionary<string, string>();
        private Dictionary<string, string> serviceNamespaces = new Dictionary<string, string>();

        public Mvc(RequestDelegate next, MvcOptions options)
        {
            _next = next;
            _options = options;
            routes = _options.Routes;

            //get a list of controllers from the assembly
            var types = Assembly.GetExecutingAssembly().GetTypes()
                .Where(type => typeof(Web.IController).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract && type.Name != "Controller").ToList();
            foreach (var type in types)
            {
                if (!type.Equals(typeof(Web.IController)))
                {
                    controllers.Add((type.FullName).ToLower(), type);
                }
            }

            types = Assembly.GetExecutingAssembly().GetTypes()
                .Where(type => typeof(Web.IService).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract && type.Name != "Service").ToList();
            foreach (var type in types)
            {
                if (!type.Equals(typeof(Web.IService)))
                {
                    services.Add((type.FullName).ToLower(), type);
                }
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (_options.IgnoreRequestBodySize == true)
            {
                context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = null;
            }
            
            var requestStart = DateTime.Now;
            var path = CleanPath(context.Request.Path.ToString());
            var paths = path.Split('/').ToArray();
            requestCount++;

            if (paths[^1].IndexOf(".") > 0)
            {
                //do not process files, but instead return a 404 error
                context.Response.StatusCode = 404;
                return;
            }

            //get parameters from request body
            var parameters = await GetParameters(context);

            if (_options.WriteDebugInfoToConsole == true)
            {
                Console.WriteLine("{0}, " + context.Request.Method + " {1}, {2} kb, # {3}", DateTime.Now.ToString("hh:mm:ss"), path, ((parameters.RequestBody.Length * sizeof(char)) / 1024.0).ToString("N1"), requestCount);
            }

            if (paths.Length > 1 && Server.servicePaths.Contains(paths[0]) == true)
            {
                //handle web API requests
                ProcessService(context, path, paths, parameters);
            }
            else
            {
                //handle controller requests
                ProcessController(context, path, paths, parameters);
            }

            //await _next.Invoke(context);
        }

        private void ProcessController(HttpContext context, string path, string[] pathParts, Web.Parameters parameters)
        {
            var html = "";
            var newpaths = path.Split('?', 2)[0].Split('/');
            var page = routes.FromControllerRoutes(context, parameters, newpaths[0].ToLower());

            if (page == null)
            {
                //page is not part of any known routes, try getting page class manually
                var className = (newpaths[0] == "" ? _options.DefaultController : newpaths[0].Replace("-", " ")).Replace(" ", "").ToLower();

                //get namespace from className
                var classNamespace = "";
                if (controllerNamespaces.ContainsKey(className))
                {
                    //find namespace from compiled list of service namespaces
                    classNamespace = controllers.Keys.FirstOrDefault(a => a.Contains(className));
                    if (classNamespace != "")
                    {
                        controllerNamespaces.Add(className, classNamespace);
                    }
                    else
                    {
                        page = new Web.Controller();
                        page.Init(context, parameters, path, pathParts);
                        html = page.Error404();
                        return;
                    }
                }
                else
                {
                    classNamespace = controllerNamespaces[className];
                }
                page = (Web.IController)Activator.CreateInstance(controllers[classNamespace]);
            }

            if (page != null)
            {
                //check request method
                if (!CanUseRequestMethod(context, page.GetType().GetMethod("Render")))
                {
                    page = new Web.Controller();
                    page.Init(context, parameters, path, pathParts);
                    html = page.BadRequest("Page does not support the '" + context.Request.Method + "' request method");
                    return;
                }

                //render page
                page.Init(context, parameters, path, pathParts);
                html = page.Render();
            }
            else
            {
                //show 404 error
                page = new Web.Controller();
                page.Init(context, parameters, path, pathParts);
                html = page.Error404();
            }

            //unload Datasilk Core
            page.Unload();
            page = null;

            //send response back to client
            if (context.Response.ContentType == null ||
                context.Response.ContentType == "")
            {
                context.Response.ContentType = "text/html";
            }
            if (context.Response.HasStarted == false)
            {
                context.Response.WriteAsync(html);
            }
        }

        private void ProcessService(HttpContext context, string path, string[] pathParts, Web.Parameters parameters)
        {
            //load service class from URL path
            string className = CleanReflectionName(pathParts[1].Replace("-", "")).ToLower();
            string methodName = pathParts.Length > 2 ? pathParts[2] : Server.defaultServiceMethod;
            if (pathParts.Length >= 4)
            {
                //path also contains extra namespace path(s)
                for (var x = 2; x < pathParts.Length - 1; x++)
                {
                    //add extra namespaces
                    className += "." + CleanReflectionName(pathParts[x].Replace("-", "")).ToLower();
                }
                //get method name at end of path
                methodName = CleanReflectionName(pathParts[^1].Replace("-", ""));
            }

            //get service type
            Type type = null;

            //get instance of service class
            var service = routes.FromServiceRoutes(context, parameters, className);
            if (service == null)
            {
                //get namespace from className
                var classNamespace = "";
                if (!serviceNamespaces.ContainsKey(className))
                {
                    //find namespace from compiled list of service namespaces
                    classNamespace = services.Keys.FirstOrDefault(a => a.Contains(className));
                    if (classNamespace != "")
                    {
                        serviceNamespaces.Add(className, classNamespace);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.WriteAsync("service does not exist");
                        return;
                    }
                }
                else
                {
                    classNamespace = serviceNamespaces[className];
                }
                service = (Web.IService)Activator.CreateInstance(services[classNamespace]);
            }

            //check if service class was found
            type = service.GetType();
            if (type == null)
            {
                context.Response.StatusCode = 404;
                context.Response.WriteAsync("service does not exist");
                return;
            }

            //update service fields
            service.Init(context, parameters, path, pathParts);

            //get class method from service type
            MethodInfo method = type.GetMethod(methodName);

            //check if method exists
            if (method == null)
            {
                context.Response.StatusCode = 404;
                context.Response.WriteAsync("Web service method " + methodName + " does not exist");
                return;
            }

            //check request method
            if(!CanUseRequestMethod(context, method))
            {
                context.Response.StatusCode = 400;
                context.Response.WriteAsync("Web service method " + methodName + " does not accept the '" + context.Request.Method + "' request method");
                return;
            }

            //try to cast params to correct types
            var paramVals = MapParameters(method.GetParameters(), parameters);

            //execute service method
            string result = (string)method.Invoke(service, paramVals);

            if (context.Response.StatusCode == 200)
            {
                //only write response if there were no errors

                if (context.Response.ContentType == null)
                {
                    if(result.IndexOf("{") < 0)
                    {
                        context.Response.ContentType = "text/plain";
                    }
                    else
                    {
                        context.Response.ContentType = "text/json";
                    }
                }
                context.Response.ContentLength = result.Length;
                if (result != null)
                {
                    context.Response.WriteAsync(result);
                }
                else
                {
                    context.Response.WriteAsync("{}");
                }
                service.Unload();
            }
        }

        #region "Helpers"
        private string CleanPath(string path)
        {
            //check for malicious path input
            if (path == "") { return path; }
            if (path[0] == '/') { path = path.Substring(1); }
            if (path.Replace("/", "").Replace("-", "").Replace("+", "").All(char.IsLetterOrDigit))
            {
                //path is clean
                return path;
            }

            //path needs to be cleaned
            return path
                .Replace("{", "")
                .Replace("}", "")
                .Replace("'", "")
                .Replace("\"", "")
                .Replace(":", "")
                .Replace("$", "")
                .Replace("!", "")
                .Replace("*", "");
        }

        private async static Task<Web.Parameters> GetParameters(HttpContext context)
        {
            var parameters = new Web.Parameters();
            string data = "";
            if (context.Request.ContentType != null && context.Request.ContentType.IndexOf("multipart/form-data") < 0 && context.Request.Body.CanRead)
            {
                //get POST data from request
                byte[] bytes = new byte[0];
                using (MemoryStream ms = new MemoryStream())
                {
                    await context.Request.Body.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }
                data = Encoding.UTF8.GetString(bytes, 0, bytes.Length).Trim();
            }

            if (data.Length > 0)
            {
                parameters.RequestBody = data;
                if (data.IndexOf("Content-Disposition") < 0 && data.IndexOf("{") >= 0 && data.IndexOf("}") > 0 && data.IndexOf(":") > 0)
                {
                    //get method parameters from POST
                    Dictionary<string, object> attr = JsonSerializer.Deserialize<Dictionary<string, object>>(data);
                    foreach (KeyValuePair<string, object> item in attr)
                    {
                        if (item.Value != null)
                        {
                            parameters.Add(item.Key.ToLower(), item.Value.ToString());
                        }
                        else
                        {
                            parameters.Add(item.Key.ToLower(), "");
                        }
                    }
                }
            }

            //get method parameters from query string
            foreach (var key in context.Request.Query.Keys)
            {
                var value = context.Request.Query[key].ToString();
                if (!parameters.ContainsKey(key))
                {
                    parameters.Add(key, value);
                }
                else
                {
                    parameters.AddTo(key, value);
                }
            }
            return parameters;
        }

        private object[] MapParameters(ParameterInfo[] methodParams, Web.Parameters parameters)
        {
            var paramVals = new object[methodParams.Length];
            for (var x = 0; x < methodParams.Length; x++)
            {
                //find correct key/value pair
                var param = "";
                var methodParamName = methodParams[x].Name.ToLower();
                var paramType = methodParams[x].ParameterType;

                foreach (var item in parameters)
                {
                    if (item.Key == methodParamName)
                    {
                        param = item.Value;
                        break;
                    }
                }

                if (param == "")
                {
                    //set default value for empty parameter
                    if (paramType == typeof(Int32))
                    {
                        param = "0";
                    }
                }

                //cast params to correct (supported) types
                if (paramType.Name != "String")
                {
                    if (int.TryParse(param, out int i) == true)
                    {
                        if (paramType.IsEnum == true)
                        {
                            //convert param value to enum
                            paramVals[x] = Enum.Parse(paramType, param);
                        }
                        else
                        {
                            //convert param value to matching method parameter number type
                            paramVals[x] = Convert.ChangeType(i, paramType);
                        }

                    }
                    else if (paramType.FullName.Contains("DateTime"))
                    {
                        //convert param value to DateTime
                        if (param == "")
                        {
                            paramVals[x] = null;
                        }
                        else
                        {
                            try
                            {
                                paramVals[x] = DateTime.Parse(param);
                            }
                            catch (Exception) { }
                        }
                    }
                    else if (paramType.IsArray)
                    {
                        //convert param value to array (of T)
                        var arr = param.Replace("[", "").Replace("]", "").Replace("\r", "").Replace("\n", "").Split(",").Select(a => { return a.Trim(); }).ToList();
                        if (paramType.FullName == "System.Int32[]")
                        {
                            //convert param values to int array
                            paramVals[x] = arr.Select(a => { return int.Parse(a); }).ToArray();
                        }
                        else
                        {
                            //convert param values to array (of matching method parameter type)
                            paramVals[x] = Convert.ChangeType(arr, paramType);
                        }


                    }
                    else if (paramType.Name.IndexOf("Dictionary") == 0)
                    {
                        //convert param value (JSON) to Dictionary
                        paramVals[x] = JsonSerializer.Deserialize<Dictionary<string, string>>(param);
                    }
                    else if (paramType.Name == "Boolean")
                    {
                        paramVals[x] = param.ToLower() == "true";
                    }
                    else
                    {
                        //convert param value to matching method parameter type
                        paramVals[x] = JsonSerializer.Deserialize(param, paramType);
                    }
                }
                else
                {
                    //matching method parameter type is string
                    paramVals[x] = param;
                }
            }
            return paramVals;
        }

        private string CleanReflectionName(string myStr)
        {
            string newStr = myStr.ToString();
            int x = 0;
            while (x < newStr.Length)
            {
                if (
                        (Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] >= Encoding.ASCII.GetBytes("a")[0] && Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] <= Encoding.ASCII.GetBytes("z")[0]) ||
                        (Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] >= Encoding.ASCII.GetBytes("A")[0] & Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] <= Encoding.ASCII.GetBytes("Z")[0]) ||
                        (Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] >= Encoding.ASCII.GetBytes("0")[0] & Encoding.ASCII.GetBytes(newStr.Substring(x, 1))[0] <= Encoding.ASCII.GetBytes("9")[0])
                    )
                {
                    x++;
                }
                else
                {
                    //remove character
                    newStr = newStr.Substring(0, x - 1) + newStr.Substring(x + 1);
                }
            }
            return newStr;
        }

        private bool CanUseRequestMethod(HttpContext context, MethodInfo method)
        {
            var reqMethod = context.Request.Method.ToLower();
            var hasReqAttr = false;
            switch (reqMethod)
            {
                case "get": hasReqAttr = method.GetCustomAttributes(typeof(Web.GETAttribute), false).Any(); break;
                case "post": hasReqAttr = method.GetCustomAttributes(typeof(Web.POSTAttribute), false).Any(); break;
                case "put": hasReqAttr = method.GetCustomAttributes(typeof(Web.PUTAttribute), false).Any(); break;
                case "head": hasReqAttr = method.GetCustomAttributes(typeof(Web.HEADAttribute), false).Any(); break;
                case "delete": hasReqAttr = method.GetCustomAttributes(typeof(Web.DELETEAttribute), false).Any(); break;
            }
            if (hasReqAttr == false)
            {
                //check if method contains other request method attributes
                if ((method.GetCustomAttributes(typeof(Web.GETAttribute), false).Any() && reqMethod != "get") ||
                    (method.GetCustomAttributes(typeof(Web.POSTAttribute), false).Any() && reqMethod != "post") ||
                    (method.GetCustomAttributes(typeof(Web.PUTAttribute), false).Any() && reqMethod != "put") ||
                    (method.GetCustomAttributes(typeof(Web.HEADAttribute), false).Any() && reqMethod != "head") ||
                    (method.GetCustomAttributes(typeof(Web.DELETEAttribute), false).Any() && reqMethod != "delete"))
                {
                    //display an error
                    return false;
                }
            }
            return true;
        }
        #endregion
    }
}