﻿using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;


public class WebAssetLoader : MonoBehaviour {
	
	public class WebItem {
		public string url;
		public int version;
		public System.Type type;
		
		private UnityEngine.Object item;
		public UnityEngine.Object Item{
			get {
				return item;
			}
			set {
				item = value;
			}
		}
		
		private int refCount = 0;
		public void AddRef()
		{
			refCount++;
		}
		
		public void Release()
		{
			refCount--;
			if(refCount > 0) return;
			
			if(Item == null) return;
			
			if(Item is AssetBundle)
			{
				((AssetBundle)Item).Unload(true);
			}
			else if(Item is Texture)
			{
				UnityEngine.Object.Destroy((Texture)Item);
			}
			Item = null;
		}
	}
	
	
	private class WebItemRequest 
	{
		public string url;
		public int version;
		public System.Type type;
		
		public System.Action<bool,WebItem> onComplete;
		public System.Action<float>  onProgress;
		
		public WebItemRequest(string url, int ver, System.Type type, System.Action<bool,WebItem> complete,System.Action<float>  onProgress)
		{
			this.url = url;
			this.version = ver;
			this.type = type;
			this.onComplete += complete;
			this.onProgress += onProgress;
		}
	}
	
	
	#region static member
	static WebAssetLoader instance;
	public static WebAssetLoader Instance {
		get {
			if(instance == null) {
				GameObject go = new GameObject("__WebAssetLoader");
				DontDestroyOnLoad(go);
				instance = go.AddComponent<WebAssetLoader>();
			}
			return instance;
		}
	}
	
	Dictionary<string, WebItem> cache = new Dictionary<string, WebItem>();
	List<WebItemRequest> pendingList = new List<WebItemRequest>();
	List<WebItemRequest> downloadingList = new List<WebItemRequest>();

#endregion
	
	void OnDisable()
	{
		foreach(var kv in cache) {
			kv.Value.Release();	
		}
		cache.Clear();
	}
	
	void StartDownload()
	{
		if(pendingList.Count > 0) {
			StartCoroutine(Downloading());
		}
	}
	
	
	IEnumerator Downloading()
	{
		while (pendingList.Count > 0) {
			WebItemRequest request = pendingList[0];
			pendingList.RemoveAt(0);
			WebItem item;
			if(cache.TryGetValue(request.url,out item) && item.Item != null) {
				item.AddRef();
				if(request.onComplete != null) request.onComplete(true, item);
				item.Release();
			}
			else {
				while (downloadingList.Count >= 4) {
					yield return null;
				}
				StartCoroutine(WorkerCoroutine(request));
			}
			yield return null;
		}
	}
	IEnumerator WorkerCoroutine(WebItemRequest request)
	{
		string filepath = GetCachePath(request.url, request.version);
		
		bool exists = System.IO.File.Exists(filepath);
		string wwwpath = request.url;
		if(exists) {
			System.Uri uri = new System.Uri(filepath);
			wwwpath = uri.AbsoluteUri;
		}
		
		using(WWW www = new WWW(System.Uri.EscapeUriString(wwwpath))) {
			downloadingList.Add(request);
			float timeout = Time.realtimeSinceStartup;
			while(!www.isDone) {
				if(request.onProgress != null) request.onProgress(www.progress);
				yield return null;
				
				if(Time.realtimeSinceStartup - timeout > 1f && www.progress == 0) {
					downloadingList.Remove(request);
					if(request.onComplete != null) request.onComplete(false, null);
					yield break;
				}
			}
			downloadingList.Remove(request);
			if(www.error != null) {
				Debug.LogWarning("cannot open url: " + wwwpath + " " + www.error);
				if(request.onComplete != null) request.onComplete(false, null);
				yield break;
			}
			WebItem item = new WebItem();
			item.url = request.url;
			item.type = request.type;
			item.version = request.version;
			
			if(item.type == typeof(Texture)) {
				item.Item = www.textureNonReadable;	
			}
			else if(item.type == typeof(AssetBundle)) {
				item.Item = www.assetBundle;	
			}
			else if(item.type == typeof(AudioClip)) {
				item.Item = www.audioClip;
			}
			
			cache[item.url] = item;
			item.AddRef();
			if(request.onComplete != null) request.onComplete(true, item);
			item.Release();
			
			//check need write to disk
			if(!exists) {
				//clear old version data
				var parentDir = System.IO.Directory.GetParent(filepath);
				var files = parentDir.GetFiles();
				foreach(var f in files) {
					f.Delete();	
				}
				//write new version data
				using (var fs = System.IO.File.OpenWrite(filepath)) {
					Debug.Log(filepath);
					fs.Write(www.bytes, 0, www.bytes.Length);
					fs.Flush();
					fs.Close();
				}
			}
		}
	}
	#region public interface
	
	public void RequestWebAsset(string url, int version, System.Type type,
		System.Action<bool, WebItem> onfinish = null,
		System.Action<float> onprogress = null)
	{
		for (int i = 0; i < pendingList.Count; i++) {
			if(pendingList[i].url.Equals(url) && pendingList[i].version == version) {
				pendingList[i].onComplete += onfinish;
				pendingList[i].onProgress += onprogress;
				return;
			}
		}
		for (int i = 0; i < downloadingList.Count; i++) {
			if(downloadingList[i].url.Equals(url)&& downloadingList[i].version == version) {
				downloadingList[i].onComplete += onfinish;
				downloadingList[i].onProgress += onprogress;
				return;
			}
		}
		
		WebItemRequest request = new WebItemRequest(url, version, type, onfinish, onprogress);
		bool needrestart = pendingList.Count == 0;
		pendingList.Add(request);
		if(needrestart) {
			StartDownload();	
		}
#if UNITY_EDITOR
//		for (int i = 0; i < pendingList.Count; i++) {
//			Debug.Log(pendingList[i].url);
//		}
#endif 
	}

	public void CancelRequest(string url, System.Action<bool, WebItem> onfinish, System.Action<float> onprogress)
	{
//		Debug.Log("Cancel Request" + url);
		if(string.IsNullOrEmpty(url)) return;
		if(pendingList.Count == 0 )return;
		
		for (int i = 0; i < pendingList.Count; i++) {
			if(pendingList[i].url.Equals(url)) {
				pendingList[i].onComplete -= onfinish;
				pendingList[i].onProgress -= onprogress;
			}
		}
		
		for (int i = 0; i < downloadingList.Count; i++) {
			if(downloadingList[i].url.Equals(url))
			{
				downloadingList[i].onComplete -= onfinish;
				downloadingList[i].onProgress -= onprogress;
			}
		}
//		}
		
#if UNITY_EDITOR
//		for (int i = 0; i < pendingList.Count; i++) {
//			Debug.Log(pendingList[i].url);
//		}
#endif 
	}
#endregion
	
	
#region helper 
	public static string MD5Hash(string input){
		using (MD5 md5Hash = MD5.Create()) {
		    // Convert the input string to a byte array and compute the hash.
		    byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
		
		    // Create a new Stringbuilder to collect the bytes
		    // and create a string.
		    StringBuilder sBuilder = new StringBuilder();
		
		    // Loop through each byte of the hashed data 
		    // and format each one as a hexadecimal string.
		    for (int i = 0; i < data.Length; i++)
		    {
		        sBuilder.Append(data[i].ToString("x2"));
		    }
		
		    // Return the hexadecimal string.
		    return sBuilder.ToString();
		}
		return "";
    }
	
	static string GetCachePath(string url, int version)
	{
		string path = System.IO.Path.Combine(Application.temporaryCachePath,"webcache");
		path = System.IO.Path.Combine(path, MD5Hash(url));
		if(!System.IO.Directory.Exists(path)){
			System.IO.Directory.CreateDirectory(path);	
		}
		return System.IO.Path.Combine(path, version.ToString("x"));
	}
	
	public static bool IsVersionCached(string url, int version)
	{
		return System.IO.File.Exists(GetCachePath(url, version));
	}
	#endregion
}
