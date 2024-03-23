using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ShaderGPT {
public abstract class GPTBase : MonoBehaviour {
	[Header("Model")]
	public Shader[] shaders;
	public Texture[] textures;
	public TextAsset configJson;
	public TextAsset tokenizerJson;
	public TextAsset testcaseJson;

	[Header("Generation")]
	public int maxLength = 2048;
	public float temperature = 0;
	public float interval = 0.1f;

	public enum Task {
		Run = 0,
		Test,
		Bake,
	}
	[Header("Task")]
	public Task task;
	public UnityEngine.UI.Text outputText;

	protected TensorNN nn;
	protected TensorContext ctx {
		get => nn.ctx;
		set { nn.ctx = value; }
	}

	protected Tokenizer tokenizer;
	protected Dictionary<string, Texture> parameters;
	protected List<int> tokens;

	private float nextTime;
	private int positionId;
	
	public void OnEnable() {
		nn = new TensorNN(){
			ctx = new TensorContext(),
			kernels = shaders.ToDictionary(x => x.name.Split('/')[1], x => x),
		};
		tokenizer = JsonUtility.FromJson<Tokenizer>(tokenizerJson.text);
		parameters = textures.ToDictionary(x => x.name, x => x);
		foreach(var pair in parameters)
			if(parameters.TryGetValue(pair.Key+".q8", out var quantTex))
				nn.quants[pair.Value] = quantTex;
		var testcase = testcaseJson ? JsonUtility.FromJson<Testcase>(testcaseJson.text) : null;

		if(task == Task.Run) {
			nextTime = Time.time;
			positionId = 0;
			if(tokens == null)
				tokens = new List<int>(testcase.input_ids);

			var text = "";
			for(int i=0; i<tokens.Count; i++)
				text += tokenizer.vocab[tokens[i]];
			if(outputText)
				outputText.text = text;
			else
				Debug.Log(text);
		} else if(task == Task.Test) {
			Test(testcase);
			Debug.Assert(ctx.TensorCount() == 0);
		} else if(task == Task.Bake) {
#if UNITY_EDITOR
			var path = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(configJson)) + ".prefab";
			Debug.Log(path);

			ctx = new TensorTracer();
			Bake();
			Debug.Assert(ctx.TensorCount() == 0);
			EditorGUIUtility.PingObject(((TensorTracer)ctx).Export(path));
#endif
		}
	}
	public void OnDisable() {
		ctx.ReleasePersistent();
	}
	public void Update() {
		if(task == Task.Run) {
			if(tokens.Count >= maxLength)
				return;
			if(Time.time < nextTime)
				return;
			nextTime = Time.time + interval;
			
			var token = Run(positionId);
			Debug.Assert(ctx.TensorCount() == 0);
			positionId = tokens.Count;
			tokens.Add(token);
			if(outputText)
				outputText.text += tokenizer.vocab[token];
			else
				Debug.Log(tokenizer.vocab[token]);
		}
	}
	public abstract int Run(int positionId);
	public abstract void Test(Testcase testcase);
	public abstract void Bake();
	
	protected Texture InputTensor(IList<int> input_ids, int position_id=0) {
		var n = input_ids.Count-position_id;
		var inputData = new float[n*4];
		for(int i=0; i<n; i++) {
			inputData[i*4+0] = input_ids[i+position_id];
			inputData[i*4+1] = i+position_id;
		}
		var input = ctx.CPUTensor(n, 1);
		ctx.SetData(input, inputData);
		return input;
	}
	protected Texture MultinomialSample(Texture lm_logits, int vocab_size, float temperature=0) {
		// becomes GreedySearch when temperature == 0
		var logits = nn.Copy(null, lm_logits,
			inputOffset:new Vector2Int(ctx.Size0(lm_logits)-1, 0), size:new Vector2Int(1, ctx.Size1(lm_logits)));
		var logits_gumbel = BatchRelease(nn.Gumbel(MarkRelease(logits), temperature));
		return BatchRelease(nn.ArgMax(MarkRelease(logits_gumbel), indexRange:new Vector2(0,vocab_size)));
	}

	// utilities
	List<Texture> releaseList = new List<Texture>();
	protected T MarkRelease<T>(T tex) where T: Texture {
		releaseList.Add(tex);
		return tex;
	}
	protected T BatchRelease<T>(T x) {
		foreach(var tex in releaseList)
			ctx.Release(tex);
		releaseList.Clear();
		return x;
	}
	protected void AssertData(RenderTexture rt, int row, float[] value, float eps) {
		var col = ctx.Size1(rt) * 4;
		var offset = (row>=0 ? row : ctx.Size0(rt)+row) * col;
		var count = Mathf.Min(col, value.Length);
		var data = ctx.GetData(rt);
		var errorL1 = 0f;
		var errorL2 = 0f;
		var errorLi = 0f;
		for(int i=0; i<count; i++) {
			var error = Mathf.Abs(data[offset+i] - value[i]);
			errorL1 += error;
			errorL2 += error*error;
			errorLi = Mathf.Max(errorLi, error);
		}
		errorL1 = errorL1/count;
		errorL2 = Mathf.Sqrt(errorL2/count);
		Debug.Log($"error: L1={errorL1}, L2={errorL2}, Li={errorLi}");
		Debug.Assert(Mathf.Abs(errorLi) < eps);
		if(Mathf.Abs(errorLi) >= eps)
			ctx.DebugTensor(rt);
	}
	protected void FixSize0(string name, int size0) {
		ctx.FixSize0(parameters[name], size0);
		if(parameters.TryGetValue($"{name}.q8", out var quantTex))
			ctx.FixSize0(quantTex, (size0+3)/4);
	}

	[System.Serializable]
	public class Tokenizer {
		public string[] vocab;
	}
	[System.Serializable]
	public class Testcase {
		public int[] input_ids;
		public float[] hidden_states;
		public float[] logits;
	}
}
}