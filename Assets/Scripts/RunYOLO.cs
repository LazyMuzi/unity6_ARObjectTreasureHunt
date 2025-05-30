using System;
using System.Collections.Generic;
using System.IO;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/*
 *  YOLO Inference Script
 *  ========================
 *
 * Place this script on the Main Camera and set the script parameters according to the tooltips.
 *
 */

public class RunYOLO : MonoBehaviour
{
    public enum InputSourceType
    {
        VideoFile,
        Webcam
    }

    [Tooltip("Drag a YOLO model .onnx file here")]
    public ModelAsset modelAsset;

    [Tooltip("Drag the classes.txt here")]
    public TextAsset classesAsset;

    [Tooltip("Create a Raw Image in the scene and link it here")]
    public RawImage displayImage;

    [Tooltip("Drag a border box texture here")]
    public Texture2D borderTexture;

    [Tooltip("Select an appropriate font for the labels")]
    public Font font;

    [Header("Input Settings")]
    [Tooltip("Select the input source: Video file or Webcam")]
    public InputSourceType inputSource = InputSourceType.VideoFile;

    [Tooltip("Name of the video in Assets/StreamingAssets (used if Input Source is VideoFile)")]
    public string videoFilename = "giraffes.mp4";

    const BackendType backend = BackendType.GPUCompute;

    private Transform displayLocation;
    private Worker worker;
    private string[] labels;
    private RenderTexture targetRT;
    private Sprite borderSprite;

    //Image size for the model
    private const int imageWidth = 640;
    private const int imageHeight = 640;

    private VideoPlayer video;
    private WebCamTexture webcamTexture;

    List<GameObject> boxPool = new();

    [Tooltip("Intersection over union threshold used for non-maximum suppression")]
    [SerializeField, Range(0, 1)]
    float iouThreshold = 0.5f;

    [Tooltip("Confidence score threshold used for non-maximum suppression")]
    [SerializeField, Range(0, 1)]
    float scoreThreshold = 0.5f;

    Tensor<float> centersToCorners;
    //bounding box data
    public struct BoundingBox
    {
        public float centerX;
        public float centerY;
        public float width;
        public float height;
        public string label;
    }

    void Start()
    {
        Application.targetFrameRate = 60;
        Screen.orientation = ScreenOrientation.LandscapeLeft;

        //Parse neural net labels
        labels = classesAsset.text.Split('\n');

        LoadModel();

        targetRT = new RenderTexture(imageWidth, imageHeight, 0);

        //Create image to display video/webcam
        displayLocation = displayImage.transform;

        SetupInput();

        borderSprite = Sprite.Create(borderTexture, new Rect(0, 0, borderTexture.width, borderTexture.height), new Vector2(borderTexture.width / 2, borderTexture.height / 2));
    }
    void LoadModel()
    {
        //Load model
        var model = ModelLoader.Load(modelAsset);

        centersToCorners = new Tensor<float>(new TensorShape(4, 4),
        new float[]
        {
                    1,      0,      1,      0,
                    0,      1,      0,      1,
                    -0.5f,  0,      0.5f,   0,
                    0,      -0.5f,  0,      0.5f
        });

        //Here we transform the output of the model1 by feeding it through a Non-Max-Suppression layer.
        var graph = new FunctionalGraph();
        var inputs = graph.AddInputs(model);
        var modelOutput = Functional.Forward(model, inputs)[0];                        //shape=(1,84,8400)
        var boxCoords = modelOutput[0, 0..4, ..].Transpose(0, 1);               //shape=(8400,4)
        var allScores = modelOutput[0, 4.., ..];                                //shape=(80,8400)
        var scores = Functional.ReduceMax(allScores, 0);                                //shape=(8400)
        var classIDs = Functional.ArgMax(allScores, 0);                                 //shape=(8400)
        var boxCorners = Functional.MatMul(boxCoords, Functional.Constant(centersToCorners));   //shape=(8400,4)
        var indices = Functional.NMS(boxCorners, scores, iouThreshold, scoreThreshold); //shape=(N)
        var coords = Functional.IndexSelect(boxCoords, 0, indices);                     //shape=(N,4)
        var labelIDs = Functional.IndexSelect(classIDs, 0, indices);                    //shape=(N)
        var selected_scores = Functional.IndexSelect(scores, 0, indices);               // <--- 이 줄 추가: 선택된 박스들의 점수

        //Create worker to run model
        worker = new Worker(graph.Compile(coords, labelIDs, selected_scores), backend);
    }

    void SetupInput()
    {
        if (inputSource == InputSourceType.VideoFile)
        {
            video = gameObject.AddComponent<VideoPlayer>();
            video.renderMode = VideoRenderMode.APIOnly;
            video.source = VideoSource.Url;
            video.url = Path.Join(Application.streamingAssetsPath, videoFilename);
            video.isLooping = true;
            video.Play();
            if (webcamTexture != null) // 다른 모드에서 사용 중이었다면 정지
            {
                webcamTexture.Stop();
                webcamTexture = null;
            }
        }
        else if (inputSource == InputSourceType.Webcam)
        {
            if (WebCamTexture.devices.Length == 0)
            {
                Debug.LogError("카메라를 찾을 수 없습니다.");
                return;
            }
            WebCamDevice device = WebCamTexture.devices[0]; // 기본 카메라 사용
            webcamTexture = new WebCamTexture(device.name, imageWidth, imageHeight, 60); // FPS 요청 추가
            webcamTexture.Play();

            if (video != null) // 다른 모드에서 사용 중이었다면 정지
            {
                video.Stop();
                Destroy(video); // VideoPlayer 컴포넌트 제거
                video = null;
            }
        }
    }

    private void FixedUpdate()
    {
        ExecuteML();
    }

    public void ExecuteML()
    {
        ClearAnnotations();

        Texture sourceTexture = null;
        bool sourceReady = false;

        if (inputSource == InputSourceType.VideoFile)
        {
            if (video != null && video.isPrepared && video.texture != null)
            {
                sourceTexture = video.texture;
                sourceReady = true;
            }
        }
        else if (inputSource == InputSourceType.Webcam)
        {
            if (webcamTexture != null && webcamTexture.isPlaying && webcamTexture.didUpdateThisFrame)
            {
                sourceTexture = webcamTexture;
                sourceReady = true;
            }
        }

        if (!sourceReady || sourceTexture == null)
        {
            return;
        }

        float sourceAspect = (float)sourceTexture.width / sourceTexture.height;
        float targetCanvasAspect = (float)imageWidth / imageHeight;

        Vector2 scale = Vector2.one;
        Vector2 offset = Vector2.zero;

        if (sourceAspect > targetCanvasAspect)
        {
            scale.y = targetCanvasAspect / sourceAspect;
            offset.y = (1 - scale.y) / 2f;
        }
        else
        {
            scale.x = sourceAspect / targetCanvasAspect;
            offset.x = (1 - scale.x) / 2f;
        }

        Graphics.Blit(sourceTexture, targetRT, scale, offset);
        displayImage.texture = targetRT;

        using Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, 3, imageHeight, imageWidth));
        TextureConverter.ToTensor(targetRT, inputTensor, default); // Sentis 최신 버전은 TextureTransform 권장
        worker.Schedule(inputTensor);

        // LoadModel에서 출력 순서에 맞게 PeekOutput 인덱스 또는 이름 사용
        // 여기서는 순서대로 0: coords, 1: labelIDs, 2: selected_scores 라고 가정
        using var coordsOutput = (worker.PeekOutput("output_0") as Tensor<float>).ReadbackAndClone(); // output_0이 coords
        using var labelIDsOutput = (worker.PeekOutput("output_1") as Tensor<int>).ReadbackAndClone();   // output_1이 labelIDs
        using var scoresOutput = (worker.PeekOutput("output_2") as Tensor<float>).ReadbackAndClone(); // output_2가 selected_scores

        float displayWidth = displayImage.rectTransform.rect.width;
        float displayHeight = displayImage.rectTransform.rect.height;

        float scaleX = displayWidth / imageWidth;
        float scaleY = displayHeight / imageHeight;

        int boxesFound = coordsOutput.shape[0];

        if (boxesFound > 0)
        {
            int bestBoxIndex = -1;
            float highestScore = -1f;

            // 가장 높은 점수를 가진 박스 찾기
            for (int n = 0; n < boxesFound; n++)
            {
                float currentScore = scoresOutput[n]; // scoresOutput에서 현재 박스의 점수를 가져옴
                if (currentScore > highestScore)
                {
                    highestScore = currentScore;
                    bestBoxIndex = n;
                }
            }

            if (bestBoxIndex != -1) // 가장 높은 점수의 박스를 찾았다면
            {
                Debug.Log($"가장 높은 점수의 객체: {labels[labelIDsOutput[bestBoxIndex]]}, 점수: {highestScore}");

                // var box = new BoundingBox
                // {
                //     centerX = coordsOutput[bestBoxIndex, 0] * scaleX - displayWidth / 2,
                //     centerY = coordsOutput[bestBoxIndex, 1] * scaleY - displayHeight / 2,
                //     width = coordsOutput[bestBoxIndex, 2] * scaleX,
                //     height = coordsOutput[bestBoxIndex, 3] * scaleY,
                //     label = labels[labelIDsOutput[bestBoxIndex]],
                // };
                // // 가장 점수가 높은 박스 하나만 그리기 (ID는 0으로 고정해도 무방, 어차피 하나만 그림)
                // DrawBox(box, 0, displayHeight * 0.05f);
            }
            else
            {
                Debug.Log("탐지된 객체가 없거나 유효한 점수를 가진 객체가 없습니다.");
            }
        }
        else
        {
            Debug.Log("탐지된 객체가 없습니다.");
        }
    }

    public void DrawBox(BoundingBox box, int id, float fontSize)
    {
        //Create the bounding box graphic or get from pool
        GameObject panel;
        if (id < boxPool.Count)
        {
            panel = boxPool[id];
            panel.SetActive(true);
        }
        else
        {
            panel = CreateNewBox(Color.yellow);
        }
        //Set box position
        panel.transform.localPosition = new Vector3(box.centerX, -box.centerY);

        //Set box size
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(box.width, box.height);

        //Set label text
        var label = panel.GetComponentInChildren<Text>();
        label.text = box.label;
        label.fontSize = (int)fontSize;
    }

    public GameObject CreateNewBox(Color color)
    {
        //Create the box and set image

        var panel = new GameObject("ObjectBox");
        panel.AddComponent<CanvasRenderer>();
        Image img = panel.AddComponent<Image>();
        img.color = color;
        img.sprite = borderSprite;
        img.type = Image.Type.Sliced;
        panel.transform.SetParent(displayLocation, false);

        //Create the label

        var text = new GameObject("ObjectLabel");
        text.AddComponent<CanvasRenderer>();
        text.transform.SetParent(panel.transform, false);
        Text txt = text.AddComponent<Text>();
        txt.font = font;
        txt.color = color;
        txt.fontSize = 40; // 이 값은 DrawBox에서 fontSize로 설정되므로 초기값은 크게 중요하지 않을 수 있습니다.
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;

        RectTransform rt2 = text.GetComponent<RectTransform>();
        rt2.offsetMin = new Vector2(20, rt2.offsetMin.y); // 왼쪽 패딩
        rt2.offsetMax = new Vector2(0, rt2.offsetMax.y);  // 오른쪽 패딩 (필요시 조정)
        rt2.offsetMin = new Vector2(rt2.offsetMin.x, 0);  // 아래쪽 패딩 (텍스트를 박스 상단에 붙이려면 조정)
        rt2.offsetMax = new Vector2(rt2.offsetMax.x, 30); // 위쪽 패딩 (텍스트 높이, 필요시 조정)
        rt2.anchorMin = new Vector2(0, 0); // 박스 내에서 앵커 설정
        rt2.anchorMax = new Vector2(1, 1); // 박스 내에서 앵커 설정 (상단에 붙이려면 anchorMin.y=1, anchorMax.y=1, pivot.y=1 등 조정 필요)


        boxPool.Add(panel);
        return panel;
    }

    public void ClearAnnotations()
    {
        foreach (var box in boxPool)
        {
            box.SetActive(false);
        }
    }

    void OnDestroy()
    {
        centersToCorners?.Dispose();
        worker?.Dispose();

        if (video != null)
        {
            video.Stop();
            // VideoPlayer는 AddComponent로 추가되었으므로 GameObject 파괴 시 자동으로 정리될 수 있지만, 명시적 Stop이 좋음
        }
        if (webcamTexture != null)
        {
            webcamTexture.Stop();
            webcamTexture = null;
        }
        if (targetRT != null)
        {
            targetRT.Release();
            Destroy(targetRT);
            targetRT = null;
        }
    }
}
