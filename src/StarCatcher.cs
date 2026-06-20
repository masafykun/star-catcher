using UnityEngine;

using System.Collections.Generic;

// スターキャッチ (Star Catcher) — コードからシーンを丸ごと構築する。
// .unity を手編集せず、RuntimeInitializeOnLoadMethod で自動生成する方針（AutoShot と共存）。
// Step 1: 正射影カメラ・地面・黄色バスケットを生成し、左右移動できるようにする。HUDに SCORE/BEST。
// Step 2: ★星を一定間隔で上から落とし、画面外で破棄する。
// Step 3: バスケットと★の重なりで +1 得点して★を消す（キャッチ）。
// Step 4: 爆弾を混ぜ、当たったらゲームオーバー → R でリスタート。
// Step 5: 難易度上昇＋見た目の調整 → 完成判定。
public class StarCatcher : MonoBehaviour
{
    // プレイフィールドの大きさ（カメラ orthographicSize と 16:9 を基準に算出）。
    public const float OrthoSize = 6f;                        // 縦半分の見える範囲
    public const float HalfWidth = OrthoSize * (16f / 9f);   // 横半分 ≒ 10.67
    public const float BasketY = -4.5f;                      // バスケットの高さ
    public const float BasketSpeed = 12f;                    // 横移動速度
    public const float BasketWidth = 2.0f;                   // バスケットの横幅

    // 落下物（★星 / 爆弾）の設定。
    public const float StarSize = 0.7f;                      // ★の大きさ
    public const float BombSize = 0.8f;                      // 爆弾の大きさ
    public const float BombChance = 0.28f;                   // 爆弾が出る確率
    public const float TopY = OrthoSize + 1f;                // 生成する高さ（画面上の外）
    public const float KillY = BasketY - 2f;                 // これより下に落ちたら破棄

    // 難易度の上昇：プレイ時間で出現間隔と落下速度を線形に変化させる（Difficulty 0→1）。
    public const float RampSeconds = 40f;                    // この秒数かけて最高難度に到達
    public const float SpawnIntervalEasy = 0.85f;            // 序盤の出現間隔
    public const float SpawnIntervalHard = 0.38f;            // 終盤の出現間隔
    public const float FallSpeedEasy = 5.0f;                 // 序盤の落下速度
    public const float FallSpeedHard = 11.0f;                // 終盤の落下速度

    Transform basket;

    // 落下中の物体を追跡（★か爆弾かを isBomb で区別。落下速度は生成時の難易度で固定）。
    class Falling { public Transform t; public bool isBomb; public float speed; }
    readonly List<Falling> items = new List<Falling>();
    float spawnTimer;

    // スコア = 加点の累計。コンボが続くほど1キャッチの加点が増える。
    int score;
    int best;
    int combo;           // 連続キャッチ数（取り逃し／爆弾でリセット）
    float playTime;      // ゲームオーバーでない間だけ進む（難易度の元）
    bool gameOver;       // 爆弾に当たると true（操作・落下・生成を停止）
    TextMesh scoreLabel; // ワールド空間のHUD（カメラ描画＝スクショに写る）
    TextMesh centerLabel; // 中央の GAME OVER 表示（通常は非表示）

    // 0(序盤)→1(終盤) の難易度。プレイ時間に比例。
    float Difficulty => Mathf.Clamp01(playTime / RampSeconds);
    int Level => 1 + Mathf.FloorToInt(Difficulty * 9f); // LV1..10

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("__StarCatcher");
        go.AddComponent<StarCatcher>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        BuildScene();
    }

    void BuildScene()
    {
        // テスト用の置物（"Cube"）が残っていれば撤去してビューをきれいにする。
        var stray = GameObject.Find("Cube");
        if (stray != null) Destroy(stray);

        // --- カメラ：正面・正射影でXY平面を見る ---
        var cam = Camera.main;
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            cam = camGo.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = OrthoSize;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.backgroundColor = new Color(0.06f, 0.05f, 0.12f); // 夜空っぽい紺
        cam.clearFlags = CameraClearFlags.SolidColor;

        // --- 地面（下端のライン） ---
        var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.position = new Vector3(0f, BasketY - 1f, 0f);
        ground.transform.localScale = new Vector3(HalfWidth * 2f, 0.4f, 1f);
        Paint(ground, new Color(0.22f, 0.20f, 0.30f));

        // --- バスケット（横長の黄色いプレート） ---
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = "Basket";
        b.transform.position = new Vector3(0f, BasketY, 0f);
        b.transform.localScale = new Vector3(BasketWidth, 0.7f, 1f);
        Paint(b, new Color(1f, 0.82f, 0.25f));
        basket = b.transform;

        // --- スコアHUD（ワールド空間テキスト。左上に配置） ---
        scoreLabel = MakeText("ScoreHUD", new Vector3(-HalfWidth + 0.3f, OrthoSize - 0.3f, 0f), TextAnchor.UpperLeft);
        UpdateScoreLabel();

        // --- 中央の GAME OVER 表示（最初は隠す） ---
        centerLabel = MakeText("CenterHUD", new Vector3(0f, 0.5f, 0f), TextAnchor.MiddleCenter);
        centerLabel.color = new Color(1f, 0.4f, 0.35f);
        centerLabel.gameObject.SetActive(false);
    }

    // ワールド空間の TextMesh を生成。orthoカメラに写るのでスクショに残る。
    TextMesh MakeText(string name, Vector3 pos, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * 0.18f;
        var tm = go.AddComponent<TextMesh>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        tm.font = font;
        tm.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        tm.fontSize = 64;
        tm.characterSize = 1f;
        tm.anchor = anchor;
        tm.color = Color.white;
        return tm;
    }

    void UpdateScoreLabel()
    {
        if (scoreLabel == null) return;
        string comboLine = combo >= 2 ? string.Format("\nCOMBO x{0}", combo) : "";
        scoreLabel.text = string.Format("SCORE {0}\nBEST {1}   LV{2}{3}", score, best, Level, comboLine);
    }

    void Update()
    {
        if (basket == null) return;

        // ゲームオーバー中は R で再開だけ受け付ける。
        if (gameOver)
        {
            if (Input.GetKeyDown(KeyCode.R)) Restart();
            return;
        }

        float dir = 0f;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) dir -= 1f;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) dir += 1f;

        // 難易度はプレイ時間で上昇（LVも連動）。
        int prevLevel = Level;
        playTime += Time.deltaTime;
        if (Level != prevLevel) UpdateScoreLabel(); // LVが上がった瞬間にHUD反映

        var pos = basket.position;
        pos.x += dir * BasketSpeed * Time.deltaTime;
        float limit = HalfWidth - BasketWidth * 0.5f; // バスケットの半幅ぶん内側でクランプ
        pos.x = Mathf.Clamp(pos.x, -limit, limit);
        basket.position = pos;

        // --- 落下物の生成（難易度で間隔短縮。確率で爆弾を混ぜる） ---
        spawnTimer -= Time.deltaTime;
        if (spawnTimer <= 0f)
        {
            spawnTimer = Mathf.Lerp(SpawnIntervalEasy, SpawnIntervalHard, Difficulty);
            SpawnItem();
        }

        // --- 落下＋キャッチ/被弾判定＋画面外掃除（後ろから走査して安全に除去） ---
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (it.t == null) { items.RemoveAt(i); continue; }
            var sp = it.t.position;
            sp.y -= it.speed * Time.deltaTime;
            it.t.position = sp;
            // 速い落下物ほど速く自転して、勢いを見た目でも伝える。
            it.t.Rotate(0f, 0f, (it.isBomb ? 60f : 120f) * (it.speed / FallSpeedEasy) * Time.deltaTime);

            float size = it.isBomb ? BombSize : StarSize;
            // バスケットと重なった：★ならキャッチ加点、爆弾ならゲームオーバー。
            if (Overlaps(basket.position, BasketWidth, 0.7f, sp, size, size))
            {
                if (it.isBomb)
                {
                    Destroy(it.t.gameObject);
                    items.RemoveAt(i);
                    GameOver();
                    return;
                }
                Catch();
                Destroy(it.t.gameObject);
                items.RemoveAt(i);
                continue;
            }

            if (sp.y < KillY)
            {
                // ★を取り逃したらコンボが途切れる（爆弾は素通りなので不問）。
                if (!it.isBomb && combo > 0) { combo = 0; UpdateScoreLabel(); }
                Destroy(it.t.gameObject);
                items.RemoveAt(i);
            }
        }
    }

    // 爆弾に当たった：操作・落下・生成を止めて中央に GAME OVER を表示。
    void GameOver()
    {
        gameOver = true;
        combo = 0;
        Paint(basket.gameObject, new Color(0.45f, 0.45f, 0.5f)); // 被弾を色で可視化
        if (centerLabel != null)
        {
            centerLabel.text = string.Format("GAME OVER\nSCORE {0}  LV{1}\nPress R", score, Level);
            centerLabel.gameObject.SetActive(true);
        }
    }

    // R リスタート：落下物を全消去し、スコアと状態を初期化して再開。
    void Restart()
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].t != null) Destroy(items[i].t.gameObject);
        items.Clear();
        score = 0;
        combo = 0;
        playTime = 0f;
        spawnTimer = 0f;
        gameOver = false;
        if (centerLabel != null) centerLabel.gameObject.SetActive(false);
        Paint(basket.gameObject, new Color(1f, 0.82f, 0.25f)); // 元の黄色に戻す
        basket.position = new Vector3(0f, BasketY, 0f);
        UpdateScoreLabel();
    }

    // 2つの軸並行ボックス（中心＋幅・高さ）が重なるか（AABB判定）。
    static bool Overlaps(Vector3 cA, float wA, float hA, Vector3 cB, float wB, float hB)
    {
        return Mathf.Abs(cA.x - cB.x) < (wA + wB) * 0.5f
            && Mathf.Abs(cA.y - cB.y) < (hA + hB) * 0.5f;
    }

    // ★をキャッチした時の加点処理。コンボが伸びるほど1キャッチの加点が増える。
    void Catch()
    {
        combo++;
        int gain = 1 + (combo - 1) / 3; // combo 1-3→+1, 4-6→+2, 7-9→+3 …
        score += gain;
        if (score > best) best = score;
        UpdateScoreLabel();
    }

    // 画面上の外から、ランダムなX位置に★または爆弾を1つ落とす。
    void SpawnItem()
    {
        bool isBomb = Random.value < BombChance;
        float size = isBomb ? BombSize : StarSize;
        var go = isBomb
            ? GameObject.CreatePrimitive(PrimitiveType.Sphere)   // 爆弾は球
            : GameObject.CreatePrimitive(PrimitiveType.Cube);    // ★は◆キューブ
        go.name = isBomb ? "Bomb" : "Star";
        float limit = HalfWidth - size; // 端に寄りすぎないよう少し内側
        float x = Random.Range(-limit, limit);
        go.transform.position = new Vector3(x, TopY, 0f);
        go.transform.localScale = new Vector3(size, size, size);
        if (!isBomb) go.transform.rotation = Quaternion.Euler(0f, 0f, 45f); // ◆向きで★っぽさを演出
        // ★は難易度が上がるほど黄→白く「加熱」させ、速さを色でも伝える。
        Color starCol = Color.Lerp(new Color(1f, 0.88f, 0.32f), new Color(1f, 1f, 0.85f), Difficulty);
        Paint(go, isBomb ? new Color(0.12f, 0.10f, 0.12f) : starCol); // 爆弾=黒/★=黄〜白

        if (isBomb)
        {
            // 危険を一目で：赤いリングを子オブジェクトとして添える。
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ring.name = "BombMark";
            Destroy(ring.GetComponent<Collider>());
            ring.transform.SetParent(go.transform, false);
            ring.transform.localScale = new Vector3(1.25f, 0.28f, 0.5f);
            ring.transform.localRotation = Quaternion.Euler(0f, 0f, 35f);
            Paint(ring, new Color(0.9f, 0.18f, 0.16f)); // 赤い帯
        }

        float fallSpeed = Mathf.Lerp(FallSpeedEasy, FallSpeedHard, Difficulty);
        items.Add(new Falling { t = go.transform, isBomb = isBomb, speed = fallSpeed });
    }

    static void Paint(GameObject go, Color c)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;
        // URP/Built-in どちらでも壊れない単純な不透明マテリアル。
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var m = new Material(shader);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        r.material = m;
    }
}
