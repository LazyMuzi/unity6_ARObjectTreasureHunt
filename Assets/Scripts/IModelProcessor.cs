using Unity.Sentis;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System;

public interface IModelProcessor
{
    // 모델의 입력 너비 (LoadModel 이후에 사용 가능해야 함)
    int InputWidth { get; }
    // 모델의 입력 높이 (LoadModel 이후에 사용 가능해야 함)
    int InputHeight { get; }

    // 모델 로드 (모델 에셋, 클래스 레이블, 백엔드, IoU/Score 임계값 등을 인자로 받을 수 있음)
    void LoadModel(ModelAsset modelAsset, TextAsset classesAsset, BackendType backend, float iouThreshold, float scoreThreshold);

    // 모델 실행 및 결과 반환 (입력 텍스처와 타겟 RenderTexture를 받아 Detection 리스트를 콜백으로 전달)
    // targetRT는 ModelManager에서 생성하여 전달
    IEnumerator Process(Texture sourceTexture, RenderTexture targetRT, Action<List<Detection>> onCompleted);

    // 리소스 해제
    void Dispose();
} 